using Couchbase.Query;
using SupportChatKafkaCouchbase.Domain;
using SupportChatKafkaCouchbase.Infra;
using SupportChatKafkaCouchbase.Repos;

namespace SupportChatKafkaCouchbase.Services;

public sealed class InactivityWorker : BackgroundService
{
    private readonly ILogger<InactivityWorker> _log;
    private readonly CouchbaseContext _ctx;
    private readonly SessionRepository _sessions;
    private readonly AgentRepository _agents;
    private readonly IClock _clock;
    private readonly ChatRoutingSettings _settings;

    public InactivityWorker(
        ILogger<InactivityWorker> log,
        CouchbaseContext ctx,
        SessionRepository sessions,
        AgentRepository agents,
        IClock clock,
        ChatRoutingSettings settings)
    {
        _log = log;
        _ctx = ctx;
        _sessions = sessions;
        _agents = agents;
        _clock = clock;
        _settings = settings;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Delay startup to allow HTTP server to bind first
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        
        var threshold = TimeSpan.FromSeconds(_settings.MaxMissedPolls * _settings.ExpectedPollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cutoff = _clock.UtcNow - threshold;

                // Needs an index; simplest is primary index during test.
                // In production, create an index on Status + LastPollAtUtc + PollCount.
                var stmt = @"
                    SELECT META(d).id AS docId, d.*
                    FROM `support`._default._default AS d
                    WHERE META(d).id LIKE 'session::%'
                      AND d.Status IN ['QueuedPrimary','QueuedOverflow','Assigned']
                      AND d.PollCount > 0
                      AND d.LastPollAtUtc < $cutoff
                ";

                var queryOptions = new QueryOptions();
                queryOptions.Parameter("cutoff", cutoff);
                
                var result = await _ctx.Cluster.QueryAsync<dynamic>(stmt, queryOptions);

                await foreach (var row in result.Rows.WithCancellation(stoppingToken))
                {
                    // docId is session::<guid>
                    var docId = (string)row.docId;
                    if (!docId.StartsWith("session::", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!Guid.TryParse(docId["session::".Length..], out var sessionId))
                        continue;

                    // Mark inactive (CAS). If it wins, release agent slot.
                    var ok = await _sessions.TryMarkInactiveAsync(sessionId, stoppingToken);
                    if (!ok) continue;

                    var s = await _sessions.GetAsync(sessionId, stoppingToken);
                    if (s?.AssignedAgentId is not null)
                    {
                        await _agents.RemoveSessionAsync(s.AssignedAgentId, sessionId, stoppingToken);
                    }

                    _log.LogInformation("Marked session {SessionId} inactive due to missed polls.", sessionId);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Inactivity scan error");
            }

            await Task.Delay(_settings.InactivityScanMs, stoppingToken);
        }
    }
}
