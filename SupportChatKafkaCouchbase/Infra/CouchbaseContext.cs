using Couchbase;
using Couchbase.KeyValue;
using Couchbase.Management.Buckets;

namespace SupportChatKafkaCouchbase.Infra;

public sealed record CouchbaseOptions(
    string ConnectionString,
    string Username,
    string Password,
    string Bucket,
    string Scope,
    string Collection);

public sealed class CouchbaseContext : IAsyncDisposable
{
    public ICluster Cluster { get; }
    public IBucket Bucket { get; }
    public IScope Scope { get; }
    public ICouchbaseCollection Collection { get; }

    private CouchbaseContext(ICluster cluster, IBucket bucket, IScope scope, ICouchbaseCollection collection)
    {
        Cluster = cluster;
        Bucket = bucket;
        Scope = scope;
        Collection = collection;
    }

    public static async Task<CouchbaseContext> ConnectAsync(CouchbaseOptions opts)
    {
        var clusterOptions = new ClusterOptions
        {
            UserName = opts.Username,
            Password = opts.Password
        };
        
        var cluster = await Couchbase.Cluster.ConnectAsync(opts.ConnectionString, clusterOptions);
        var bucket = await cluster.BucketAsync(opts.Bucket);
        var scope = bucket.Scope(opts.Scope);
        var collection = scope.Collection(opts.Collection);

        return new CouchbaseContext(cluster, bucket, scope, collection);
    }

    public async ValueTask DisposeAsync()
    {
        await Cluster.DisposeAsync();
    }
}
