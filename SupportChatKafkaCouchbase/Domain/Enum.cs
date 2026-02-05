namespace SupportChatKafkaCouchbase.Domain;

public enum Seniority
{
    Junior,
    MidLevel,
    Senior,
    TeamLead,
    OverflowJunior
}

public enum ChatSessionStatus
{
    QueuedPrimary,
    QueuedOverflow,
    Assigned,
    Inactive,
    Closed,
    Refused
}
