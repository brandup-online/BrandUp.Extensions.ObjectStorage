namespace BrandUp.Extensions.ObjectStorage.Internals;

internal class S3ObjectStorage(IObjectStorageClient client) : IObjectStorageContext
{
    public Task<ObjectItem<TMetadata>?> FindAsync<TMetadata>(Guid objectId, CancellationToken cancellationToken = default)
        where TMetadata : class, IObjectMetadata
        => client.GetBucket<TMetadata>().FindOneAsync(objectId, cancellationToken);

    public Task<Stream?> ReadAsync<TMetadata>(Guid objectId, CancellationToken cancellationToken = default)
        where TMetadata : class, IObjectMetadata
        => client.GetBucket<TMetadata>().OpenReadAsync(objectId, cancellationToken);

    public Task<ObjectItem<TMetadata>> UploadAsync<TMetadata>(Guid objectId, TMetadata metadata, Stream stream, CancellationToken cancellationToken = default)
        where TMetadata : class, IObjectMetadata
        => client.GetBucket<TMetadata>().UploadAsync(objectId, metadata, stream, cancellationToken);

    public Task<bool> DeleteAsync<TMetadata>(Guid objectId, CancellationToken cancellationToken = default)
        where TMetadata : class, IObjectMetadata
        => client.GetBucket<TMetadata>().DeleteOneAsync(objectId, cancellationToken);
}
