namespace BrandUp.Extensions.ObjectStorage;

public class FakeObjectStorageClient(FakeObjectStore store) : IObjectStorageClient
{
    readonly Dictionary<Type, (string BucketName, string? Prefix)> _mappings = [];

    public void AddMapping<TMetadata>(string destination)
        where TMetadata : class, IObjectMetadata
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        destination = destination.Trim().Trim('/').ToLower();

        var slash = destination.IndexOf('/');
        var bucketName = slash == -1 ? destination : destination[..slash];
        var prefix = slash == -1 ? null : destination[(slash + 1)..];

        _mappings[typeof(TMetadata)] = (bucketName, prefix);
    }

    public IObjectBucket GetBucket(string bucketName)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucketName);
        return new FakeObjectBucket(bucketName.ToLower(), store);
    }

    public IObjectBucket<TMetadata> GetBucket<TMetadata>()
        where TMetadata : class, IObjectMetadata
    {
        var type = typeof(TMetadata);
        if (!_mappings.TryGetValue(type, out var mapping))
            throw new InvalidOperationException($"No mapping configured for type {type.FullName}.");

        return new FakeObjectBucket<TMetadata>(mapping.BucketName, mapping.Prefix, store);
    }

    public Task<IReadOnlyList<BucketInfo>> ListBucketsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(store.ListBuckets());

    public Task CreateBucketAsync(string bucketName, Action<BucketSettings>? configure = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucketName);

        var settings = new BucketSettings();
        configure?.Invoke(settings);
        store.CreateBucket(bucketName.ToLower(), settings);

        return Task.CompletedTask;
    }

    public Task DropBucketAsync(string bucketName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucketName);
        store.DropBucket(bucketName.ToLower());
        return Task.CompletedTask;
    }
}
