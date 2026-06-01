namespace BrandUp.Extensions.ObjectStorage.Internals;

internal interface IS3Client
{
    Task<S3StorageObject> UploadAsync(string bucketName, string objectKey, IDictionary<string, string> metadata, Stream stream, CancellationToken cancellationToken);
    Task<S3StorageObject?> FindAsync(string bucketName, string objectKey, IEnumerable<string> metadataKeys, CancellationToken cancellationToken);
    Task<Stream?> ReadAsync(string bucketName, string objectKey, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(string bucketName, string objectKey, CancellationToken cancellationToken);
}

internal record S3StorageObject(string Key, long Size, string Etag, IDictionary<string, string> Metadata);
