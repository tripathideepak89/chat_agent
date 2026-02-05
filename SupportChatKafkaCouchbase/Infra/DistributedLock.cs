using Couchbase.Core.Exceptions.KeyValue;
using Couchbase.KeyValue;

namespace SupportChatKafkaCouchbase.Infra;

public interface IDistributedLock
{
    Task<bool> TryAcquireAsync(string lockId, TimeSpan ttl, CancellationToken ct);
    Task ReleaseAsync(string lockId, CancellationToken ct);
}

public sealed class CouchbaseDistributedLock : IDistributedLock
{
    private readonly ICouchbaseCollection _col;

    public CouchbaseDistributedLock(CouchbaseContext ctx) => _col = ctx.Collection;

    public async Task<bool> TryAcquireAsync(string lockId, TimeSpan ttl, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            // Insert fails if exists => lock held by another replica
            await _col.InsertAsync(lockId, new { createdAt = DateTimeOffset.UtcNow }, opts =>
            {
                opts.Expiry(ttl);
            });
            return true;
        }
        catch (DocumentExistsException)
        {
            return false;
        }
    }

    public async Task ReleaseAsync(string lockId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await _col.RemoveAsync(lockId);
        }
        catch (DocumentNotFoundException)
        {
            // already expired/removed
        }
    }
}
