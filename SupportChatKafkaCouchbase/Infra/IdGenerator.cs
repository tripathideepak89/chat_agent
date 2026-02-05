namespace SupportChatKafkaCouchbase.Infra;

public static class DocIds
{
    public static string Session(Guid id) => $"session::{id:D}";
    public static string Agent(string id) => $"agent::{id}";
    public static string AgentLock(string id) => $"lock::agent::{id}";
}
