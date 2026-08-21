namespace BrandUp.Extensions.ObjectStorage;

public interface IObjectStorageContext
{
    Task<ObjectItem<TMetadata>?> FindAsync<TMetadata>(Guid objectId, CancellationToken cancellationToken = default) where TMetadata : class, IObjectMetadata;
    Task<Stream?> ReadAsync<TMetadata>(Guid objectId, CancellationToken cancellationToken = default) where TMetadata : class, IObjectMetadata;
    Task<ObjectItem<TMetadata>> UploadAsync<TMetadata>(Guid objectId, TMetadata metadata, Stream stream, CancellationToken cancellationToken = default) where TMetadata : class, IObjectMetadata;
    Task<bool> DeleteAsync<TMetadata>(Guid objectId, CancellationToken cancellationToken = default) where TMetadata : class, IObjectMetadata;

    /// <summary>
    /// Server-side copy between the buckets of this storage: the payload never travels through the caller.
    /// The copy is written with <paramref name="targetMetadata"/>, and the HTTP headers are carried over from
    /// the source object. Returns <see langword="false"/> when the source object does not exist.
    /// See <c>CopyToAsync</c> on <see cref="IObjectBucket{TMetadata, TKey}"/> for the full semantics.
    /// </summary>
    Task<bool> CopyAsync<TSourceMetadata, TTargetMetadata>(
        Guid sourceObjectId, Guid targetObjectId, TTargetMetadata targetMetadata,
        CancellationToken cancellationToken = default)
        where TSourceMetadata : class, IObjectMetadata
        where TTargetMetadata : class, IObjectMetadata;
}
