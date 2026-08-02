namespace BrandUp.Extensions.ObjectStorage.Internals;

internal interface IS3Client
{
    // Object operations
    Task<S3StorageObject> UploadAsync(string bucketName, string objectKey, IDictionary<string, string> metadata, Stream stream, UploadOptions? options, CancellationToken cancellationToken);
    Task<S3StorageObject?> FindAsync(string bucketName, string objectKey, IEnumerable<string> metadataKeys, CancellationToken cancellationToken);
    Task<Stream?> ReadAsync(string bucketName, string objectKey, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(string bucketName, string objectKey, CancellationToken cancellationToken);
    IAsyncEnumerable<ObjectListItem> ListObjectsAsync(string bucketName, string? prefix, CancellationToken cancellationToken);
    Task<Uri> GetPresignedUrlAsync(string bucketName, string objectKey, TimeSpan expiresIn, bool forWrite, string? contentType, CancellationToken cancellationToken);

    // Bucket operations
    Task<bool> BucketExistsAsync(string bucketName, CancellationToken cancellationToken);
    Task CreateBucketAsync(string bucketName, CancellationToken cancellationToken);
    Task DeleteBucketAsync(string bucketName, CancellationToken cancellationToken);
    Task<IReadOnlyList<BucketInfo>> ListBucketsAsync(CancellationToken cancellationToken);

    // Bucket settings
    Task<BucketVersioning> GetVersioningAsync(string bucketName, CancellationToken cancellationToken);
    Task SetVersioningAsync(string bucketName, BucketVersioning versioning, CancellationToken cancellationToken);
    Task<BucketAccess> GetAccessAsync(string bucketName, CancellationToken cancellationToken);
    Task SetAccessAsync(string bucketName, BucketAccess access, CancellationToken cancellationToken);
    Task<IReadOnlyList<LifecycleRule>> GetLifecycleAsync(string bucketName, CancellationToken cancellationToken);
    Task SetLifecycleAsync(string bucketName, IReadOnlyList<LifecycleRule> rules, CancellationToken cancellationToken);
}

internal record S3StorageObject(string Key, long Size, string Etag, IDictionary<string, string> Metadata);
