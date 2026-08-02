namespace BrandUp.Extensions.ObjectStorage;

public interface IObjectBucket
{
    string Name { get; }

    Task<bool> ExistsAsync(CancellationToken cancellationToken = default);
    Task<BucketSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task UpdateSettingsAsync(Action<BucketSettings> configure, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists objects of the bucket, optionally narrowed by a raw key prefix. A typed bucket
    /// (<see cref="IObjectBucket{TMetadata, TKey}"/>) whose mapping declares a key prefix scopes the
    /// listing to that prefix; without one it sees the entire bucket, including objects of other
    /// mappings. Returned keys are the raw object keys.
    /// </summary>
    IAsyncEnumerable<ObjectListItem> ListAsync(string? keyPrefix = null, CancellationToken cancellationToken = default);
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
    Task<ObjectItem<TMetadata, TKey>> UploadAsync(TKey objectId, TMetadata metadata, Stream content, UploadOptions? options, CancellationToken cancellationToken = default);
    Task<bool> DeleteOneAsync(TKey objectId, CancellationToken cancellationToken = default);

    /// <summary>Temporary public link for downloading the object, valid for <paramref name="expiresIn"/>.</summary>
    Task<Uri> GetPresignedReadUrlAsync(TKey objectId, TimeSpan expiresIn, CancellationToken cancellationToken = default);

    /// <summary>
    /// Temporary public link for uploading the object via HTTP PUT, valid for <paramref name="expiresIn"/>.
    /// When <paramref name="contentType"/> is set the uploader must send the same <c>Content-Type</c> header.
    /// An object uploaded this way carries no metadata: its properties read back as defaults.
    /// </summary>
    Task<Uri> GetPresignedWriteUrlAsync(TKey objectId, TimeSpan expiresIn, string? contentType = null, CancellationToken cancellationToken = default);
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
    new Task<ObjectItem<TMetadata>> UploadAsync(Guid objectId, TMetadata metadata, Stream content, UploadOptions? options, CancellationToken cancellationToken = default);
}
