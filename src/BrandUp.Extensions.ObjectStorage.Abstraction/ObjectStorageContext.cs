using BrandUp.Extensions.ObjectStorage.Internals;

namespace BrandUp.Extensions.ObjectStorage;

/// <summary>
/// Base class for a typed storage context: a set of buckets bound to one storage connection (one cloud account).
/// Declare buckets as properties marked with <see cref="BucketAttribute"/>; they are filled in when the context
/// is created by the DI container.
/// </summary>
/// <example>
/// <code>
/// public class MediaStorage : ObjectStorageContext
/// {
///     [Bucket]           public IObjectBucket&lt;PhotoMetadata&gt; Photos { get; private set; } = null!;
///     [Bucket("videos")] public IObjectBucket&lt;VideoMetadata&gt; Videos { get; private set; } = null!;
/// }
/// </code>
/// </example>
public abstract class ObjectStorageContext : IObjectStorageContext
{
    readonly Dictionary<Type, IObjectBucket> _buckets = [];
    IObjectStorageClient? _client;
    IReadOnlyDictionary<string, Action<BucketSettings>>? _bucketSettings;

    /// <summary>
    /// Client of the connection this context is bound to. Use it for bucket-level administration
    /// (create/drop/list) within the same account.
    /// </summary>
    public IObjectStorageClient Client => _client ?? throw NotInitialized();

    /// <summary>Buckets of this context, keyed by metadata type.</summary>
    public IReadOnlyCollection<IObjectBucket> Buckets
        => _client is null ? throw NotInitialized() : _buckets.Values;

    /// <summary>Bucket serving <typeparamref name="TMetadata"/>, keyed by <see cref="Guid"/>.</summary>
    public IObjectBucket<TMetadata> Bucket<TMetadata>()
        where TMetadata : class, IObjectMetadata
        => Bucket(typeof(TMetadata)) as IObjectBucket<TMetadata>
            ?? throw KeyMismatch(typeof(TMetadata), typeof(Guid));

    /// <summary>Bucket serving <typeparamref name="TMetadata"/>, keyed by <typeparamref name="TKey"/>.</summary>
    public IObjectBucket<TMetadata, TKey> Bucket<TMetadata, TKey>()
        where TMetadata : class, IObjectMetadata
        where TKey : notnull
        => Bucket(typeof(TMetadata)) as IObjectBucket<TMetadata, TKey>
            ?? throw KeyMismatch(typeof(TMetadata), typeof(TKey));

    InvalidOperationException KeyMismatch(Type metadataType, Type keyType)
        => new($"Bucket for {metadataType.Name} in {GetType().Name} is not keyed by {keyType.Name}; " +
            "check the key type declared by the context property.");

    /// <summary>Bucket serving <paramref name="metadataType"/>.</summary>
    public IObjectBucket Bucket(Type metadataType)
    {
        ArgumentNullException.ThrowIfNull(metadataType);

        if (_client is null)
            throw NotInitialized();

        return _buckets.TryGetValue(metadataType, out var bucket)
            ? bucket
            : throw new InvalidOperationException(
                $"Storage context {GetType().Name} has no bucket for metadata type {metadataType.FullName}.");
    }

    /// <summary>
    /// Creates the buckets of this context that do not exist yet. Uses the resolved names, so provisioning code
    /// never has to repeat them — unlike <see cref="IObjectStorageClient.CreateBucketAsync"/>, which takes a
    /// physical bucket name.
    /// <para>
    /// Only buckets with explicitly declared settings are managed: ConfigureBucket at registration or an entry in
    /// the <c>Buckets</c> configuration section (an empty one means "create with defaults"). Buckets without them are
    /// assumed to be provisioned elsewhere and are left alone — unless <paramref name="configure"/> is passed,
    /// which opts every bucket of the context in.
    /// </para>
    /// <para>
    /// Settings are applied at creation only: an existing bucket is never reconfigured, so changing them later
    /// has no effect on buckets that already exist.
    /// </para>
    /// </summary>
    /// <param name="configure">
    /// Applied on top of the declared settings; passing it also makes the call cover buckets that have no
    /// declared settings.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task EnsureBucketsAsync(
        Action<BucketSettings>? configure = null,
        CancellationToken cancellationToken = default)
    {
        if (_client is null)
            throw NotInitialized();

        // Several properties may point to one bucket with different key prefixes.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var bucket in _buckets.Values)
        {
            if (!names.Add(bucket.Name))
                continue;

            var declared = _bucketSettings is not null && _bucketSettings.TryGetValue(bucket.Name, out var d) ? d : null;
            if (declared is null && configure is null)
                continue;   // not managed by this context

            var settings = declared is null ? configure : declared + configure;

            if (await bucket.ExistsAsync(cancellationToken))
                continue;

            try
            {
                await _client.CreateBucketAsync(bucket.Name, settings, cancellationToken);
            }
            catch (ObjectStorageException e) when (e.IsBucketAlreadyExists)
            {
                // Check and create are not atomic: another process provisioning the same storage won the race.
            }
        }
    }

    // Called by the DI registration right after construction: the context itself stays free of any
    // knowledge about connections, options and credentials.
    internal void Initialize(
        IObjectStorageClient client,
        StorageModel model,
        IReadOnlyDictionary<string, Action<BucketSettings>>? bucketSettings = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(model);

        _bucketSettings = bucketSettings;

        foreach (var property in model.Properties)
        {
            var bucket = (IObjectBucket)property.CreateBucket(client);
            property.SetValue(this, bucket);
            _buckets[property.MetadataType] = bucket;
        }

        _client = client;
    }

    InvalidOperationException NotInitialized()
        => new($"Storage context {GetType().Name} is not initialized. Register it with AddObjectStorage<{GetType().Name}>(…).");

    #region IObjectStorageContext members

    public Task<ObjectItem<TMetadata>?> FindAsync<TMetadata>(Guid objectId, CancellationToken cancellationToken = default)
        where TMetadata : class, IObjectMetadata
        => Bucket<TMetadata>().FindOneAsync(objectId, cancellationToken);

    public Task<Stream?> ReadAsync<TMetadata>(Guid objectId, CancellationToken cancellationToken = default)
        where TMetadata : class, IObjectMetadata
        => Bucket<TMetadata>().OpenReadAsync(objectId, cancellationToken);

    public Task<ObjectItem<TMetadata>> UploadAsync<TMetadata>(Guid objectId, TMetadata metadata, Stream stream, CancellationToken cancellationToken = default)
        where TMetadata : class, IObjectMetadata
        => Bucket<TMetadata>().UploadAsync(objectId, metadata, stream, cancellationToken);

    public Task<bool> DeleteAsync<TMetadata>(Guid objectId, CancellationToken cancellationToken = default)
        where TMetadata : class, IObjectMetadata
        => Bucket<TMetadata>().DeleteOneAsync(objectId, cancellationToken);

    public Task<bool> CopyAsync<TSourceMetadata, TTargetMetadata>(
        Guid sourceObjectId, Guid targetObjectId, TTargetMetadata targetMetadata,
        CancellationToken cancellationToken = default)
        where TSourceMetadata : class, IObjectMetadata
        where TTargetMetadata : class, IObjectMetadata
        => Bucket<TSourceMetadata>().CopyToAsync(
            sourceObjectId, Bucket<TTargetMetadata>(), targetObjectId, targetMetadata, options: null, cancellationToken);

    #endregion

    #region Typed-key facade

    public Task<ObjectItem<TMetadata, TKey>?> FindAsync<TMetadata, TKey>(TKey objectId, CancellationToken cancellationToken = default)
        where TMetadata : class, IObjectMetadata
        where TKey : notnull
        => Bucket<TMetadata, TKey>().FindOneAsync(objectId, cancellationToken);

    public Task<Stream?> ReadAsync<TMetadata, TKey>(TKey objectId, CancellationToken cancellationToken = default)
        where TMetadata : class, IObjectMetadata
        where TKey : notnull
        => Bucket<TMetadata, TKey>().OpenReadAsync(objectId, cancellationToken);

    public Task<ObjectItem<TMetadata, TKey>> UploadAsync<TMetadata, TKey>(TKey objectId, TMetadata metadata, Stream stream, CancellationToken cancellationToken = default)
        where TMetadata : class, IObjectMetadata
        where TKey : notnull
        => Bucket<TMetadata, TKey>().UploadAsync(objectId, metadata, stream, cancellationToken);

    public Task<bool> DeleteAsync<TMetadata, TKey>(TKey objectId, CancellationToken cancellationToken = default)
        where TMetadata : class, IObjectMetadata
        where TKey : notnull
        => Bucket<TMetadata, TKey>().DeleteOneAsync(objectId, cancellationToken);

    /// <summary>Server-side copy between two buckets of this context, each with its own key type.</summary>
    public Task<bool> CopyAsync<TSourceMetadata, TSourceKey, TTargetMetadata, TTargetKey>(
        TSourceKey sourceObjectId, TTargetKey targetObjectId, TTargetMetadata targetMetadata,
        CancellationToken cancellationToken = default)
        where TSourceMetadata : class, IObjectMetadata
        where TSourceKey : notnull
        where TTargetMetadata : class, IObjectMetadata
        where TTargetKey : notnull
        => Bucket<TSourceMetadata, TSourceKey>().CopyToAsync(
            sourceObjectId, Bucket<TTargetMetadata, TTargetKey>(), targetObjectId, targetMetadata, options: null, cancellationToken);

    #endregion
}
