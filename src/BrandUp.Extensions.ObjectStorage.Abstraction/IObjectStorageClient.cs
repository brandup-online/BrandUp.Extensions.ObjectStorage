namespace BrandUp.Extensions.ObjectStorage;

public interface IObjectStorageClient
{
    IObjectBucket GetBucket(string bucketName);
    IObjectBucket<TMetadata> GetBucket<TMetadata>() where TMetadata : class, IObjectMetadata;

    /// <summary>Bucket for <typeparamref name="TMetadata"/> keyed by <typeparamref name="TKey"/> instead of <see cref="Guid"/>.</summary>
    IObjectBucket<TMetadata, TKey> GetBucket<TMetadata, TKey>()
        where TMetadata : class, IObjectMetadata
        where TKey : notnull;

    Task<IReadOnlyList<BucketInfo>> ListBucketsAsync(CancellationToken cancellationToken = default);
    Task CreateBucketAsync(string bucketName, Action<BucketSettings>? configure = null, CancellationToken cancellationToken = default);
    Task DropBucketAsync(string bucketName, CancellationToken cancellationToken = default);
}
