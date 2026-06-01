namespace BrandUp.Extensions.ObjectStorage;

public interface IObjectStorageClient
{
    IObjectBucket GetBucket(string bucketName);
    IObjectBucket<TMetadata> GetBucket<TMetadata>() where TMetadata : class, IObjectMetadata;

    Task<IReadOnlyList<BucketInfo>> ListBucketsAsync(CancellationToken cancellationToken = default);
    Task CreateBucketAsync(string bucketName, Action<BucketSettings>? configure = null, CancellationToken cancellationToken = default);
    Task DropBucketAsync(string bucketName, CancellationToken cancellationToken = default);
}
