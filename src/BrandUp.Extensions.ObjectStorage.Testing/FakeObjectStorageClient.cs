using System.Net;
using BrandUp.Extensions.ObjectStorage.Internals;

namespace BrandUp.Extensions.ObjectStorage;

public class FakeObjectStorageClient(FakeObjectStore store) : IObjectStorageClient
{
    readonly Dictionary<Type, (string BucketName, string? Prefix)> _mappings = [];

    public void AddMapping<TMetadata>(string destination)
        where TMetadata : class, IObjectMetadata
        => AddMapping(typeof(TMetadata), destination);

    public void AddMapping(Type metadataType, string destination)
    {
        ArgumentNullException.ThrowIfNull(metadataType);

        // Same rules as the real AddMapping path, so the fake rejects what production would reject;
        // lower-casing mirrors ObjectMapping.Create.
        destination = DestinationValidator.Normalize(destination, nameof(destination)).ToLower();

        _mappings[metadataType] = DestinationValidator.Split(destination);
    }

    public IObjectBucket GetBucket(string bucketName)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucketName);
        return new FakeObjectBucket(bucketName.ToLower(), store);
    }

    public IObjectBucket<TMetadata> GetBucket<TMetadata>()
        where TMetadata : class, IObjectMetadata
    {
        var mapping = GetMapping(typeof(TMetadata));
        return new FakeObjectBucket<TMetadata>(mapping.BucketName, mapping.Prefix, store);
    }

    public IObjectBucket<TMetadata, TKey> GetBucket<TMetadata, TKey>()
        where TMetadata : class, IObjectMetadata
        where TKey : notnull
    {
        // The Guid shape stays the richer historic one, whichever overload asked for it.
        if (typeof(TKey) == typeof(Guid))
            return (IObjectBucket<TMetadata, TKey>)GetBucket<TMetadata>();

        var mapping = GetMapping(typeof(TMetadata));
        return new FakeObjectBucket<TMetadata, TKey>(mapping.BucketName, mapping.Prefix, store);
    }

    (string BucketName, string? Prefix) GetMapping(Type metadataType)
        => _mappings.TryGetValue(metadataType, out var mapping)
            ? mapping
            : throw new InvalidOperationException($"No mapping configured for type {metadataType.FullName}.");

    public Task<IReadOnlyList<BucketInfo>> ListBucketsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(store.ListBuckets());

    public Task CreateBucketAsync(string bucketName, Action<BucketSettings>? configure = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucketName);

        var settings = new BucketSettings();
        configure?.Invoke(settings);

        try
        {
            store.CreateBucket(bucketName.ToLower(), settings);
        }
        catch (InvalidOperationException e)
        {
            // Same shape as a real provider, so callers can handle the duplicate the same way in tests.
            throw new ObjectStorageException(e.Message, HttpStatusCode.Conflict, "BucketAlreadyOwnedByYou", e);
        }

        return Task.CompletedTask;
    }

    public Task DropBucketAsync(string bucketName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucketName);
        store.DropBucket(bucketName.ToLower());
        return Task.CompletedTask;
    }
}
