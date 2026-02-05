namespace SupportChatKafkaCouchbase.Api;

public sealed record CreateSessionRequest(string? CustomerReference);
public sealed record CreateSessionResponse(bool Accepted, Guid SessionId, string Queue, string Message);

public sealed record PollResponse(string Status, string? AgentId, string? Team);
