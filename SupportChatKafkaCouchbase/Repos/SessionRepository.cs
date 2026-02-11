using Couchbase.Core.Exceptions;
using Couchbase.Core.Exceptions.KeyValue;
using Couchbase.KeyValue;
using Couchbase.Query;
using SupportChatKafkaCouchbase.Domain;
using SupportChatKafkaCouchbase.Infra;

namespace SupportChatKafkaCouchbase.Repos;

public sealed class SessionRepository
{
    private readonly ICouchbaseCollection _col;
    private readonly CouchbaseContext _ctx;

    public SessionRepository(CouchbaseContext ctx)
    {
        _col = ctx.Collection;
        _ctx = ctx;
    }

    public async Task CreateAsync(ChatSession session, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _col.InsertAsync(DocIds.Session(session.Id), session);
    }

    public async Task<ChatSession?> GetAsync(Guid id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var res = await _col.GetAsync(DocIds.Session(id));
            return res.ContentAs<ChatSession>();
        }
        catch (DocumentNotFoundException)
        {
            return null;
        }
    }

    public async Task UpsertAsync(ChatSession session, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _col.UpsertAsync(DocIds.Session(session.Id), session);
    }

    public async Task<bool> TryMarkAssignedAsync(Guid sessionId, string agentId, string team, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // CAS loop: only one replica wins
        for (int attempt = 0; attempt < 10; attempt++)
        {
            IGetResult get;
            try
            {
                get = await _col.GetAsync(DocIds.Session(sessionId));
            }
            catch (DocumentNotFoundException)
            {
                return false;
            }

            var session = get.ContentAs<ChatSession>();

            // Idempotency: if already assigned or inactive/closed, do nothing and "successfully handled"
            if (session.Status is ChatSessionStatus.Assigned)
                return true;

            if (session.Status is ChatSessionStatus.Inactive or ChatSessionStatus.Closed or ChatSessionStatus.Refused)
                return false;

            session.Status = ChatSessionStatus.Assigned;
            session.AssignedAgentId = agentId;
            session.AssignedTeam = team;

            try
            {
                await _col.ReplaceAsync(DocIds.Session(sessionId), session, opts => opts.Cas(get.Cas));
                return true;
            }
            catch (CasMismatchException)
            {
                // somebody else updated; retry
            }
        }

        return false;
    }

    public async Task<bool> TouchPollAsync(Guid sessionId, DateTimeOffset utcNow, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        for (int attempt = 0; attempt < 10; attempt++)
        {
            IGetResult get;
            try
            {
                get = await _col.GetAsync(DocIds.Session(sessionId));
            }
            catch (DocumentNotFoundException)
            {
                return false;
            }

            var session = get.ContentAs<ChatSession>();

            if (session.Status is ChatSessionStatus.Refused)
                return false;

            session.PollCount++;
            session.LastPollAtUtc = utcNow;

            try
            {
                await _col.ReplaceAsync(DocIds.Session(sessionId), session, opts => opts.Cas(get.Cas));
                return true;
            }
            catch (CasMismatchException) { }
        }

        return false;
    }

    public async Task<bool> TryMarkInactiveAsync(Guid sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        for (int attempt = 0; attempt < 10; attempt++)
        {
            IGetResult get;
            try
            {
                get = await _col.GetAsync(DocIds.Session(sessionId));
            }
            catch (DocumentNotFoundException)
            {
                return false;
            }

            var session = get.ContentAs<ChatSession>();

            if (session.Status is ChatSessionStatus.Inactive or ChatSessionStatus.Closed or ChatSessionStatus.Refused)
                return false;

            // If never polled, do not mark inactive
            if (session.PollCount <= 0)
                return false;

            session.Status = ChatSessionStatus.Inactive;

            try
            {
                await _col.ReplaceAsync(DocIds.Session(sessionId), session, opts => opts.Cas(get.Cas));
                return true;
            }
            catch (CasMismatchException) { }
        }

        return false;
    }

    public async Task<int> CountQueuedAsync(string queueHint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var status = queueHint == "Overflow"
            ? ChatSessionStatus.QueuedOverflow
            : ChatSessionStatus.QueuedPrimary;

        var stmt = @"
            SELECT COUNT(*) AS cnt
            FROM `support`._default._default AS d
            WHERE META(d).id LIKE 'session::%'
              AND d.Status = $status
        ";

        var queryOptions = new QueryOptions();
        queryOptions.Parameter("status", status.ToString());

        var result = await _ctx.Cluster.QueryAsync<Dictionary<string, long>>(stmt, queryOptions);
        var rows = await result.Rows.ToListAsync(ct);

        return rows.Count > 0 ? (int)rows[0]["cnt"] : 0;
    }
}
