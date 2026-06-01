using Microsoft.Extensions.Options;

namespace BrandUp.Extensions.ObjectStorage.Internals;

internal class S3ObjectStorageClient : IObjectStorageClient
{
    readonly IS3Client _s3;
    readonly Dictionary<Type, ObjectMapping> _mappings = [];

    public S3ObjectStorageClient(IS3Client s3, IOptions<ObjectStorageMappingsOptions> mappingsOptions)
    {
        _s3 = s3;
        foreach (var (type, destination) in mappingsOptions.Value.Destinations)
            _mappings[type] = ObjectMapping.Create(type, destination);
    }

    public IObjectBucket GetBucket(string bucketName)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucketName);
        return new S3ObjectBucket(bucketName, _s3);
    }

    public IObjectBucket<TMetadata> GetBucket<TMetadata>()
        where TMetadata : class, IObjectMetadata
    {
        var type = typeof(TMetadata);
        if (!_mappings.TryGetValue(type, out var mapping))
            throw new InvalidOperationException($"No mapping configured for type {type.FullName}.");

        return new S3ObjectBucket<TMetadata>(_s3, mapping);
    }

    public Task<IReadOnlyList<BucketInfo>> ListBucketsAsync(CancellationToken cancellationToken = default)
        => _s3.ListBucketsAsync(cancellationToken);

    public async Task CreateBucketAsync(string bucketName, Action<BucketSettings>? configure = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucketName);

        await _s3.CreateBucketAsync(bucketName, cancellationToken);

        if (configure != null)
            await GetBucket(bucketName).UpdateSettingsAsync(configure, cancellationToken);
    }

    public Task DropBucketAsync(string bucketName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucketName);
        return _s3.DeleteBucketAsync(bucketName, cancellationToken);
    }
}
