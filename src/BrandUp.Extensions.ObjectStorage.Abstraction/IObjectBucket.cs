namespace BrandUp.Extensions.ObjectStorage;

public interface IObjectBucket
{
    string Name { get; }

    Task<bool> ExistsAsync(CancellationToken cancellationToken = default);
    Task<BucketSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task UpdateSettingsAsync(Action<BucketSettings> configure, CancellationToken cancellationToken = default);
}

/// <summary>
/// Typed bucket keyed by <typeparamref name="TKey"/>. Supported key types: <see cref="Guid"/>,
/// <see cref="string"/>, <see cref="int"/>, <see cref="long"/> and types implementing
/// <see cref="IObjectKey"/> (see <see cref="ObjectKey"/> for structured keys).
/// </summary>
public interface IObjectBucket<TMetadata, TKey> : IObjectBucket
    where TMetadata : class, IObjectMetadata
    where TKey : notnull
{
    Task<ObjectItem<TMetadata, TKey>?> FindOneAsync(TKey objectId, CancellationToken cancellationToken = default);
    Task<Stream?> OpenReadAsync(TKey objectId, CancellationToken cancellationToken = default);
    Task<ObjectItem<TMetadata, TKey>> UploadAsync(TKey objectId, TMetadata metadata, Stream content, CancellationToken cancellationToken = default);
    Task<bool> DeleteOneAsync(TKey objectId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Bucket keyed by <see cref="Guid"/> — the historic default. The <c>new</c> members narrow the item type
/// to <see cref="ObjectItem{TMetadata}"/>, so existing code keeps compiling unchanged.
/// </summary>
public interface IObjectBucket<TMetadata> : IObjectBucket<TMetadata, Guid>
    where TMetadata : class, IObjectMetadata
{
    new Task<ObjectItem<TMetadata>?> FindOneAsync(Guid objectId, CancellationToken cancellationToken = default);
    new Task<ObjectItem<TMetadata>> UploadAsync(Guid objectId, TMetadata metadata, Stream content, CancellationToken cancellationToken = default);
}
