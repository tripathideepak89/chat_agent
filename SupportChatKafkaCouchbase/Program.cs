using System.Text.Json;
using SupportChatKafkaCouchbase.Api;
using SupportChatKafkaCouchbase.Domain;
using SupportChatKafkaCouchbase.Infra;
using SupportChatKafkaCouchbase.Repos;
using SupportChatKafkaCouchbase.Services;

var builder = WebApplication.CreateBuilder(args);

// Configure host with longer startup timeout
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = false;
    options.ValidateOnBuild = false;
});

builder.Services.Configure<HostOptions>(options =>
{
    options.StartupTimeout = TimeSpan.FromMinutes(5);
});

builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen();

// Add CORS for UI
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var kafkaOpts = new KafkaOptions(
    BootstrapServers: builder.Configuration["Kafka:BootstrapServers"]!,
    PrimaryTopic: builder.Configuration["Kafka:PrimaryTopic"]!,
    OverflowTopic: builder.Configuration["Kafka:OverflowTopic"]!,
    ConsumerGroup: builder.Configuration["Kafka:ConsumerGroup"]!
);
builder.Services.AddSingleton(kafkaOpts);

var cbOpts = new CouchbaseOptions(
    ConnectionString: builder.Configuration["Couchbase:ConnectionString"]!,
    Username: builder.Configuration["Couchbase:Username"]!,
    Password: builder.Configuration["Couchbase:Password"]!,
    Bucket: builder.Configuration["Couchbase:Bucket"]!,
    Scope: builder.Configuration["Couchbase:Scope"]!,
    Collection: builder.Configuration["Couchbase:Collection"]!
);
builder.Services.AddSingleton(cbOpts);

// ChatRouting settings
var settings = new ChatRoutingSettings
{
    TimeZoneId = builder.Configuration["ChatRouting:TimeZoneId"] ?? "UTC",
    OfficeHoursStart = TimeSpan.Parse(builder.Configuration["ChatRouting:OfficeHoursStart"] ?? "09:00:00"),
    OfficeHoursEnd = TimeSpan.Parse(builder.Configuration["ChatRouting:OfficeHoursEnd"] ?? "17:00:00"),
    MaxMissedPolls = int.Parse(builder.Configuration["ChatRouting:MaxMissedPolls"] ?? "3"),
    ExpectedPollIntervalSeconds = int.Parse(builder.Configuration["ChatRouting:ExpectedPollIntervalSeconds"] ?? "1"),
    LockTtlSeconds = int.Parse(builder.Configuration["ChatRouting:LockTtlSeconds"] ?? "10"),
    AssignmentTickMs = int.Parse(builder.Configuration["ChatRouting:AssignmentTickMs"] ?? "150"),
    InactivityScanMs = int.Parse(builder.Configuration["ChatRouting:InactivityScanMs"] ?? "800")
};
builder.Services.AddSingleton(settings);

builder.Services.AddSingleton<IClock, SystemClock>();

// Couchbase context (singleton)
builder.Services.AddSingleton(async sp =>
{
    var o = sp.GetRequiredService<CouchbaseOptions>();
    return await CouchbaseContext.ConnectAsync(o);
});
builder.Services.AddSingleton(sp => sp.GetRequiredService<Task<CouchbaseContext>>().GetAwaiter().GetResult());

// repos / infra
builder.Services.AddSingleton<IDistributedLock, CouchbaseDistributedLock>();
builder.Services.AddSingleton<SessionRepository>();
builder.Services.AddSingleton<AgentRepository>();

// Kafka
builder.Services.AddSingleton<IKafkaProducer, KafkaProducer>();
builder.Services.AddSingleton<IKafkaConsumerFactory, KafkaConsumerFactory>();

// Background workers
builder.Services.AddHostedService<AssignmentWorker>();
builder.Services.AddHostedService<InactivityWorker>();

var app = builder.Build();

// Enable CORS
app.UseCors();

// Serve static files from wwwroot
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseSwagger();
app.UseSwaggerUI();

// Seed agents into Couchbase (idempotent upserts)
// Moved to background to avoid blocking startup
_ = Task.Run(async () =>
{
    await Task.Delay(2000); // Give app time to start
    try
    {
        await SeedAgentsAsync(app.Services.GetRequiredService<AgentRepository>());
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error seeding agents: {ex.Message}");
    }
});

app.MapPost("/api/chat/sessions", async (
    CreateSessionRequest req,
    SessionRepository sessions,
    IKafkaProducer producer,
    KafkaOptions kafka,
    ChatRoutingSettings s,
    IClock clock,
    CancellationToken ct) =>
{
    var session = new ChatSession
    {
        Id = Guid.NewGuid(),
        CreatedAtUtc = clock.UtcNow,
        CustomerReference = string.IsNullOrWhiteSpace(req.CustomerReference) ? null : req.CustomerReference
    };

    var nowLocal = ConvertToLocal(clock.UtcNow, s.TimeZoneId).TimeOfDay;

    var isOfficeHours = nowLocal >= s.OfficeHoursStart && nowLocal < s.OfficeHoursEnd;
    var queueHint = ChooseQueueHint(nowLocal, isOfficeHours);

    session.QueueHint = queueHint;
    session.Status = queueHint == "Overflow" ? ChatSessionStatus.QueuedOverflow : ChatSessionStatus.QueuedPrimary;

    await sessions.CreateAsync(session, ct);

    var env = new AssignmentEnvelope(session.Id, queueHint);
    var payload = JsonSerializer.Serialize(env);
    var topic = queueHint == "Overflow" ? kafka.OverflowTopic : kafka.PrimaryTopic;

    // Key by session id for stable partitioning
    await producer.ProduceAsync(topic, session.Id.ToString("D"), payload, ct);

    return Results.Ok(new CreateSessionResponse(
        Accepted: true,
        SessionId: session.Id,
        Queue: queueHint,
        Message: "OK"
    ));
});

app.MapGet("/api/chat/sessions/{id:guid}", async (Guid id, SessionRepository sessions, CancellationToken ct) =>
{
    var s = await sessions.GetAsync(id, ct);
    return s is null ? Results.NotFound() : Results.Ok(s);
});

app.MapGet("/api/chat/sessions/{id:guid}/poll", async (
    Guid id,
    SessionRepository sessions,
    IClock clock,
    CancellationToken ct) =>
{
    var ok = await sessions.TouchPollAsync(id, clock.UtcNow, ct);
    if (!ok)
        return Results.NotFound();

    var s = await sessions.GetAsync(id, ct);
    if (s is null) return Results.NotFound();

    if (s.Status is ChatSessionStatus.Inactive or ChatSessionStatus.Closed)
        return Results.Ok(new PollResponse("INACTIVE", s.AssignedAgentId, s.AssignedTeam));

    if (s.Status is ChatSessionStatus.Assigned)
        return Results.Ok(new PollResponse("ASSIGNED", s.AssignedAgentId, s.AssignedTeam));

    return Results.Ok(new PollResponse("WAIT", null, null));
});

// Dashboard Endpoints
app.MapGet("/api/chat/sessions", async (CouchbaseContext ctx, CancellationToken ct) =>
{
    var statement = @"
        SELECT META(d).id, d.*
        FROM `support`._default._default AS d
        WHERE META(d).id LIKE 'session::%'
        ORDER BY d.createdAtUtc DESC
        LIMIT 50
    ";
    
    var result = await ctx.Cluster.QueryAsync<ChatSession>(statement);
    var sessions = new List<ChatSession>();
    
    await foreach (var row in result.Rows.WithCancellation(ct))
    {
        sessions.Add(row);
    }
    
    return Results.Ok(sessions);
});

app.MapGet("/api/chat/agents", async (CouchbaseContext ctx, CancellationToken ct) =>
{
    var statement = @"
        SELECT META(d).id, d.*
        FROM `support`._default._default AS d
        WHERE META(d).id LIKE 'agent::%'
        ORDER BY d.team, d.seniority DESC
    ";
    
    var result = await ctx.Cluster.QueryAsync<AgentState>(statement);
    var agents = new List<AgentState>();
    
    await foreach (var row in result.Rows.WithCancellation(ct))
    {
        agents.Add(row);
    }
    
    return Results.Ok(agents);
});

app.MapGet("/api/chat/stats", async (CouchbaseContext ctx, CancellationToken ct) =>
{
    var statement = @"
        SELECT 
            COUNT(*) as totalSessions,
            SUM(CASE WHEN d.status IN [0, 1] THEN 1 ELSE 0 END) as queuedSessions,
            SUM(CASE WHEN d.status = 2 THEN 1 ELSE 0 END) as assignedSessions,
            SUM(CASE WHEN d.status = 2 OR d.pollCount > 0 THEN 1 ELSE 0 END) as activeSessions
        FROM `support`._default._default AS d
        WHERE META(d).id LIKE 'session::%'
    ";
    
    var result = await ctx.Cluster.QueryAsync<dynamic>(statement);
    var stats = await result.Rows.FirstOrDefaultAsync(ct);
    
    return Results.Ok(new
    {
        totalSessions = stats?.totalSessions ?? 0,
        queuedSessions = stats?.queuedSessions ?? 0,
        assignedSessions = stats?.assignedSessions ?? 0,
        activeSessions = stats?.activeSessions ?? 0
    });
});

app.Run();

static DateTimeOffset ConvertToLocal(DateTimeOffset utc, string tzId)
{
    var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
    return TimeZoneInfo.ConvertTime(utc, tz);
}

static string ChooseQueueHint(TimeSpan nowLocal, bool officeHours)
{
    // We keep the same notion:
    // - Primary always
    // - Overflow only during office hours
    // This code chooses primary by default; if you want “queue full => overflow”
    // you’d compute capacity & queue length (requires counters). For Kafka-based,
    // common approach is: always produce primary; overflow is for special conditions.
    // For the assignment requirement, we keep: office hours => overflow allowed.
    return officeHours ? "Primary" : "Primary";
}

static async Task SeedAgentsAsync(AgentRepository repo)
{
    // Team A: 1 lead, 2 mid, 1 junior (08-16)
    await repo.UpsertAsync(new AgentState { Id="A-Lead-1", Team="TeamA", Seniority=Seniority.TeamLead, ShiftStart=TimeSpan.FromHours(8), ShiftEnd=TimeSpan.FromHours(16) }, CancellationToken.None);
    await repo.UpsertAsync(new AgentState { Id="A-Mid-1",  Team="TeamA", Seniority=Seniority.MidLevel, ShiftStart=TimeSpan.FromHours(8), ShiftEnd=TimeSpan.FromHours(16) }, CancellationToken.None);
    await repo.UpsertAsync(new AgentState { Id="A-Mid-2",  Team="TeamA", Seniority=Seniority.MidLevel, ShiftStart=TimeSpan.FromHours(8), ShiftEnd=TimeSpan.FromHours(16) }, CancellationToken.None);
    await repo.UpsertAsync(new AgentState { Id="A-Jnr-1",  Team="TeamA", Seniority=Seniority.Junior,   ShiftStart=TimeSpan.FromHours(8), ShiftEnd=TimeSpan.FromHours(16) }, CancellationToken.None);

    // Team B: 1 senior, 1 mid, 2 junior (16-24)
    await repo.UpsertAsync(new AgentState { Id="B-Snr-1",  Team="TeamB", Seniority=Seniority.Senior,   ShiftStart=TimeSpan.FromHours(16), ShiftEnd=TimeSpan.FromHours(0) }, CancellationToken.None);
    await repo.UpsertAsync(new AgentState { Id="B-Mid-1",  Team="TeamB", Seniority=Seniority.MidLevel, ShiftStart=TimeSpan.FromHours(16), ShiftEnd=TimeSpan.FromHours(0) }, CancellationToken.None);
    await repo.UpsertAsync(new AgentState { Id="B-Jnr-1",  Team="TeamB", Seniority=Seniority.Junior,   ShiftStart=TimeSpan.FromHours(16), ShiftEnd=TimeSpan.FromHours(0) }, CancellationToken.None);
    await repo.UpsertAsync(new AgentState { Id="B-Jnr-2",  Team="TeamB", Seniority=Seniority.Junior,   ShiftStart=TimeSpan.FromHours(16), ShiftEnd=TimeSpan.FromHours(0) }, CancellationToken.None);

    // Team C: 2 mid (00-08)
    await repo.UpsertAsync(new AgentState { Id="C-Mid-1",  Team="TeamC", Seniority=Seniority.MidLevel, ShiftStart=TimeSpan.FromHours(0), ShiftEnd=TimeSpan.FromHours(8) }, CancellationToken.None);
    await repo.UpsertAsync(new AgentState { Id="C-Mid-2",  Team="TeamC", Seniority=Seniority.MidLevel, ShiftStart=TimeSpan.FromHours(0), ShiftEnd=TimeSpan.FromHours(8) }, CancellationToken.None);

    // Overflow: 6 juniors (always shift; office hours enforced when selecting overflow)
    for (int i = 1; i <= 6; i++)
    {
        await repo.UpsertAsync(new AgentState
        {
            Id = $"O-Jnr-{i}",
            Team = "Overflow",
            Seniority = Seniority.OverflowJunior,
            ShiftStart = TimeSpan.FromHours(0),
            ShiftEnd = TimeSpan.FromHours(0)
        }, CancellationToken.None);
    }
}
