using System.Text.Json.Serialization;

namespace SupportChatKafkaCouchbase.Domain;

public sealed class ChatSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public ChatSessionStatus Status { get; set; } = ChatSessionStatus.QueuedPrimary;

    public DateTimeOffset LastPollAtUtc { get; set; } = DateTimeOffset.MinValue;
    public long PollCount { get; set; } = 0;

    public string? AssignedAgentId { get; set; }
    public string? AssignedTeam { get; set; }

    public string? CustomerReference { get; set; }

    // Stored so we can route consistently even if time changes later
    public string? QueueHint { get; set; } // "Primary" | "Overflow"
}

public sealed class AgentState
{
    public required string Id { get; init; }
    public required string Team { get; init; }
    public required Seniority Seniority { get; init; }

    public required TimeSpan ShiftStart { get; init; }
    public required TimeSpan ShiftEnd { get; init; }

    public int MaxConcurrentChats { get; init; } = 10;

    public HashSet<Guid> ActiveSessionIds { get; set; } = new();

    [JsonIgnore]
    public int ActiveChats => ActiveSessionIds.Count;
}
