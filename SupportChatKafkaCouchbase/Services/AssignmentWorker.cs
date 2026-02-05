using System.Text.Json;
using Confluent.Kafka;
using SupportChatKafkaCouchbase.Domain;
using SupportChatKafkaCouchbase.Infra;
using SupportChatKafkaCouchbase.Repos;

namespace SupportChatKafkaCouchbase.Services;

public sealed record AssignmentEnvelope(Guid SessionId, string QueueHint);

public sealed class ChatRoutingSettings
{
    public required string TimeZoneId { get; init; }
    public required TimeSpan OfficeHoursStart { get; init; }
    public required TimeSpan OfficeHoursEnd { get; init; }

    public required int LockTtlSeconds { get; init; }
    public required int AssignmentTickMs { get; init; }

    public required int MaxMissedPolls { get; init; }
    public required int ExpectedPollIntervalSeconds { get; init; }

    public required int InactivityScanMs { get; init; }
}

public sealed class AssignmentWorker : BackgroundService
{
    private readonly ILogger<AssignmentWorker> _log;
    private readonly IKafkaConsumerFactory _consumerFactory;
    private readonly KafkaOptions _kafka;
    private readonly SessionRepository _sessions;
    private readonly AgentRepository _agents;
    private readonly IDistributedLock _lock;
    private readonly IClock _clock;
    private readonly ChatRoutingSettings _settings;

    // in-memory RR cursor (safe per instance; fairness isn’t critical)
    private readonly Dictionary<string, int> _rr = new();

    public AssignmentWorker(
        ILogger<AssignmentWorker> log,
        IKafkaConsumerFactory consumerFactory,
        KafkaOptions kafka,
        SessionRepository sessions,
        AgentRepository agents,
        IDistributedLock distributedLock,
        IClock clock,
        ChatRoutingSettings settings)
    {
        _log = log;
        _consumerFactory = consumerFactory;
        _kafka = kafka;
        _sessions = sessions;
        _agents = agents;
        _lock = distributedLock;
        _clock = clock;
        _settings = settings;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var consumer = _consumerFactory.Create();
        consumer.Subscribe(new[] { _kafka.PrimaryTopic, _kafka.OverflowTopic });

        _log.LogInformation("AssignmentWorker subscribed to Kafka topics.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cr = consumer.Consume(TimeSpan.FromMilliseconds(_settings.AssignmentTickMs));
                if (cr is null) continue;

                var env = JsonSerializer.Deserialize<AssignmentEnvelope>(cr.Message.Value);
                if (env is null)
                {
                    consumer.Commit(cr);
                    continue;
                }

                var ok = await TryAssignAsync(env, stoppingToken);

                // Even if no capacity, we still commit to avoid “hot looping” the same message forever.
                // If you want strict FIFO waiting, you can re-produce with delay/backoff instead.
                consumer.Commit(cr);

                if (!ok)
                {
                    _log.LogDebug("Session {SessionId} not assigned (no capacity/eligibility).", env.SessionId);
                }
            }
            catch (ConsumeException ex)
            {
                _log.LogError(ex, "Kafka consume error");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Assignment loop error");
            }
        }
    }

    private async Task<bool> TryAssignAsync(AssignmentEnvelope env, CancellationToken ct)
    {
        var session = await _sessions.GetAsync(env.SessionId, ct);
        if (session is null) return false;

        // idempotency: already assigned handled
        if (session.Status == ChatSessionStatus.Assigned) return true;
        if (session.Status is ChatSessionStatus.Inactive or ChatSessionStatus.Closed or ChatSessionStatus.Refused)
            return false;

        var nowLocal = ConvertToLocal(_clock.UtcNow, _settings.TimeZoneId).TimeOfDay;

        var targetTeam = env.QueueHint == "Overflow"
            ? "Overflow"
            : DetermineCurrentTeam(nowLocal);

        if (targetTeam == "Overflow" && !IsOfficeHours(nowLocal))
            return false;

        var teamAgents = await _agents.GetTeamAgentsAsync(targetTeam, ct);
        if (teamAgents.Count == 0) return false;

        // prefer junior -> mid -> senior -> lead
        var order = new[]
        {
            Seniority.Junior,
            Seniority.OverflowJunior,
            Seniority.MidLevel,
            Seniority.Senior,
            Seniority.TeamLead
        };

        foreach (var s in order)
        {
            var group = teamAgents
                .Where(a => a.Seniority == s)
                .Where(a => CanReceiveNewChats(a, nowLocal))
                .ToList();

            if (group.Count == 0) continue;

            var key = $"{targetTeam}:{s}";
            _rr.TryGetValue(key, out var idx);

            for (int attempt = 0; attempt < group.Count; attempt++)
            {
                var pick = group[(idx + attempt) % group.Count];

                // Distributed lock per agent across replicas
                var lockId = DocIds.AgentLock(pick.Id);
                var gotLock = await _lock.TryAcquireAsync(lockId, TimeSpan.FromSeconds(_settings.LockTtlSeconds), ct);
                if (!gotLock) continue;

                try
                {
                    // Re-check session status just before assignment (another replica might have assigned)
                    var latest = await _sessions.GetAsync(env.SessionId, ct);
                    if (latest is null) return false;
                    if (latest.Status == ChatSessionStatus.Assigned) return true;
                    if (latest.Status is ChatSessionStatus.Inactive or ChatSessionStatus.Closed or ChatSessionStatus.Refused)
                        return false;

                    // Try add session to agent (CAS protected)
                    var added = await _agents.TryAddSessionAsync(pick.Id, env.SessionId, ct);
                    if (!added) continue;

                    // Mark session assigned (CAS protected)
                    var marked = await _sessions.TryMarkAssignedAsync(env.SessionId, pick.Id, pick.Team, ct);
                    if (!marked)
                    {
                        // rollback agent slot if session couldn’t be assigned
                        await _agents.RemoveSessionAsync(pick.Id, env.SessionId, ct);
                        continue;
                    }

                    _rr[key] = (idx + attempt + 1) % group.Count;
                    _log.LogInformation("Assigned session {SessionId} to agent {AgentId} ({Team})",
                        env.SessionId, pick.Id, pick.Team);

                    return true;
                }
                finally
                {
                    await _lock.ReleaseAsync(lockId, ct);
                }
            }

            _rr[key] = (idx + 1) % group.Count;
        }

        return false;
    }

    private bool IsOfficeHours(TimeSpan localTime)
        => localTime >= _settings.OfficeHoursStart && localTime < _settings.OfficeHoursEnd;

    private static DateTimeOffset ConvertToLocal(DateTimeOffset utc, string tzId)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
        return TimeZoneInfo.ConvertTime(utc, tz);
    }

    private static bool IsWithinShift(TimeSpan localTime, TimeSpan start, TimeSpan end)
    {
        if (start == end) return true;
        if (start < end) return localTime >= start && localTime < end;
        return localTime >= start || localTime < end; // crosses midnight
    }

    private static bool CanReceiveNewChats(AgentState a, TimeSpan nowLocal)
        => IsWithinShift(nowLocal, a.ShiftStart, a.ShiftEnd);

    private static string DetermineCurrentTeam(TimeSpan nowLocal)
    {
        if (nowLocal >= TimeSpan.FromHours(0) && nowLocal < TimeSpan.FromHours(8)) return "TeamC";
        if (nowLocal >= TimeSpan.FromHours(8) && nowLocal < TimeSpan.FromHours(16)) return "TeamA";
        return "TeamB";
    }
}
