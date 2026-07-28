namespace BrandUp.Extensions.ObjectStorage;

public interface IObjectStorageContext
{
    Task<ObjectItem<TMetadata>?> FindAsync<TMetadata>(Guid objectId, CancellationToken cancellationToken = default) where TMetadata : class, IObjectMetadata;
    Task<Stream?> ReadAsync<TMetadata>(Guid objectId, CancellationToken cancellationToken = default) where TMetadata : class, IObjectMetadata;
    Task<ObjectItem<TMetadata>> UploadAsync<TMetadata>(Guid objectId, TMetadata metadata, Stream stream, CancellationToken cancellationToken = default) where TMetadata : class, IObjectMetadata;
    Task<bool> DeleteAsync<TMetadata>(Guid objectId, CancellationToken cancellationToken = default) where TMetadata : class, IObjectMetadata;
}
