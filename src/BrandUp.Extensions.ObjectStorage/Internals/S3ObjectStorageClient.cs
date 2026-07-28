namespace BrandUp.Extensions.ObjectStorage.Internals;

/// <summary>
/// Client of a single connection with its own set of typed mappings. Contexts sharing a connection get their
/// own client instance (cheap) over the shared <see cref="IS3Client"/>.
/// </summary>
internal class S3ObjectStorageClient : IObjectStorageClient
{
    readonly IS3Client _s3;
    readonly Dictionary<Type, ObjectMapping> _mappings = [];

    public S3ObjectStorageClient(IS3Client s3, IReadOnlyDictionary<Type, string> destinations)
    {
        ArgumentNullException.ThrowIfNull(s3);
        ArgumentNullException.ThrowIfNull(destinations);

        _s3 = s3;
        foreach (var (type, destination) in destinations)
            _mappings[type] = ObjectMapping.Create(type, destination);
    }

    public IObjectBucket GetBucket(string bucketName)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucketName);
        return new S3ObjectBucket(bucketName, _s3);
    }

    public IObjectBucket<TMetadata> GetBucket<TMetadata>()
        where TMetadata : class, IObjectMetadata
        => new S3ObjectBucket<TMetadata>(_s3, GetMapping(typeof(TMetadata)));

    public IObjectBucket<TMetadata, TKey> GetBucket<TMetadata, TKey>()
        where TMetadata : class, IObjectMetadata
        where TKey : notnull
    {
        // The Guid shape stays the richer historic one, whichever overload asked for it.
        if (typeof(TKey) == typeof(Guid))
            return (IObjectBucket<TMetadata, TKey>)GetBucket<TMetadata>();

        return new S3ObjectBucket<TMetadata, TKey>(_s3, GetMapping(typeof(TMetadata)));
    }

    ObjectMapping GetMapping(Type metadataType)
        => _mappings.TryGetValue(metadataType, out var mapping)
            ? mapping
            : throw new InvalidOperationException($"No mapping configured for type {metadataType.FullName}.");

    public Task<IReadOnlyList<BucketInfo>> ListBucketsAsync(CancellationToken cancellationToken = default)
        => _s3.ListBucketsAsync(cancellationToken);

    public async Task CreateBucketAsync(string bucketName, Action<BucketSettings>? configure = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucketName);

        await _s3.CreateBucketAsync(bucketName, cancellationToken);

        if (configure is null)
            return;

        // A fresh bucket has the service defaults, so nothing needs to be read back: apply the delegate to the
        // defaults and write only what it actually changed (an empty delegate writes nothing at all).
        var desired = new BucketSettings();
        configure(desired);

        await S3ObjectBucket.WriteChangedAsync(_s3, bucketName, new BucketSettings(), desired, cancellationToken);
    }

    public Task DropBucketAsync(string bucketName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucketName);
        return _s3.DeleteBucketAsync(bucketName, cancellationToken);
    }
}
