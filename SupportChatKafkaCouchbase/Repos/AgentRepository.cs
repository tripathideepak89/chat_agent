using Couchbase;
using Couchbase.Core.Exceptions;
using Couchbase.Core.Exceptions.KeyValue;
using Couchbase.KeyValue;
using Couchbase.Query;
using SupportChatKafkaCouchbase.Domain;
using SupportChatKafkaCouchbase.Infra;

namespace SupportChatKafkaCouchbase.Repos;

public static class Capacity
{
    public static double Multiplier(Seniority s) => s switch
    {
        Seniority.Junior => 0.4,
        Seniority.MidLevel => 0.6,
        Seniority.Senior => 0.8,
        Seniority.TeamLead => 0.5,
        Seniority.OverflowJunior => 0.4,
        _ => 0.4
    };

    public static int AgentCapacity(AgentState a)
        => (int)Math.Floor(a.MaxConcurrentChats * Multiplier(a.Seniority));
}

public sealed class AgentRepository
{
    private readonly ICouchbaseCollection _col;
    private readonly CouchbaseContext _ctx;

    public AgentRepository(CouchbaseContext ctx)
    {
        _ctx = ctx;
        _col = ctx.Collection;
    }

    public async Task UpsertAsync(AgentState agent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _col.UpsertAsync(DocIds.Agent(agent.Id), agent);
    }

    public async Task<AgentState?> GetAsync(string agentId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var res = await _col.GetAsync(DocIds.Agent(agentId));
            return res.ContentAs<AgentState>();
        }
        catch (DocumentNotFoundException)
        {
            return null;
        }
    }

    public async Task<List<AgentState>> GetTeamAgentsAsync(string team, CancellationToken ct)
    {
        // Use N1QL query over default collection by doc id prefix
        // NOTE: In real systems you'd store agents in a dedicated collection + create indexes.
        // For this assignment: keep it simple and query by known IDs isn't possible, so we query.
        ct.ThrowIfCancellationRequested();

        // Assumes default collection and that agent docs contain "Team".
        // You MUST create a primary index or a proper index in Couchbase for this query.
        // Example: CREATE PRIMARY INDEX ON `support`._default._default;
        var cluster = _ctx.Cluster;

        var statement = @"
            SELECT d.*
            FROM `support`._default._default AS d
            WHERE META(d).id LIKE 'agent::%'
              AND d.team = $team
        ";

        var queryOptions = new QueryOptions();
        queryOptions.Parameter("team", team);
        
        var result = await cluster.QueryAsync<AgentState>(statement, queryOptions);
        return await result.Rows.ToListAsync(ct);
    }

    public async Task<bool> TryAddSessionAsync(string agentId, Guid sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        for (int attempt = 0; attempt < 10; attempt++)
        {
            IGetResult get;
            try
            {
                get = await _col.GetAsync(DocIds.Agent(agentId));
            }
            catch (DocumentNotFoundException)
            {
                return false;
            }

            var agent = get.ContentAs<AgentState>();
            var cap = Capacity.AgentCapacity(agent);

            if (agent.ActiveSessionIds.Contains(sessionId))
                return true; // idempotent

            if (agent.ActiveSessionIds.Count >= cap)
                return false;

            agent.ActiveSessionIds.Add(sessionId);

            try
            {
                await _col.ReplaceAsync(DocIds.Agent(agentId), agent, opts => opts.Cas(get.Cas));
                return true;
            }
            catch (CasMismatchException) { }
        }

        return false;
    }

    public async Task<bool> RemoveSessionAsync(string agentId, Guid sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        for (int attempt = 0; attempt < 10; attempt++)
        {
            IGetResult get;
            try
            {
                get = await _col.GetAsync(DocIds.Agent(agentId));
            }
            catch (DocumentNotFoundException)
            {
                return false;
            }

            var agent = get.ContentAs<AgentState>();
            if (!agent.ActiveSessionIds.Remove(sessionId))
                return true; // idempotent

            try
            {
                await _col.ReplaceAsync(DocIds.Agent(agentId), agent, opts => opts.Cas(get.Cas));
                return true;
            }
            catch (CasMismatchException) { }
        }

        return false;
    }

    public async Task<int> GetTeamCapacityAsync(string team, CancellationToken ct)
    {
        var agents = await GetTeamAgentsAsync(team, ct);
        return agents.Sum(a => Capacity.AgentCapacity(a));
    }
}
