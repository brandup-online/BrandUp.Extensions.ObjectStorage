using System.Net;
using BrandUp.Extensions.ObjectStorage.Internals;

namespace BrandUp.Extensions.ObjectStorage;

public class FakeObjectBucket(string name, FakeObjectStore store) : IObjectBucket
{
    protected readonly FakeObjectStore Store = store;

    public string Name { get; } = name;

    public Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Store.BucketExists(Name));

    public async IAsyncEnumerable<ObjectListItem> ListAsync(
        string? keyPrefix = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Deferred like production: NoSuchBucket surfaces at first MoveNext, not at the call site,
        // and the token is honored per item — so cancellation-sensitive code behaves the same way
        // against the fake as against S3.
        RequireBucket();

        foreach (var item in Store.ListObjects(Name, BuildListPrefix(keyPrefix)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }

        // The store is synchronous; this no-op await keeps the async-iterator shape without CS1998.
        await Task.CompletedTask;
    }

    /// <summary>A typed bucket narrows the listing to its own mapping prefix.</summary>
    private protected virtual string? BuildListPrefix(string? keyPrefix) => keyPrefix;

    public Task<BucketSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        RequireBucket();
        return Task.FromResult(Store.GetSettings(Name));
    }

    public Task UpdateSettingsAsync(Action<BucketSettings> configure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);

        RequireBucket();
        Store.UpdateSettings(Name, configure);
        return Task.CompletedTask;
    }

    /// <summary>Same shape as a real provider when the bucket does not exist (404 NoSuchBucket).</summary>
    private protected void RequireBucket()
    {
        if (!Store.BucketExists(Name))
            throw new ObjectStorageException(
                $"Bucket '{Name}' does not exist.", HttpStatusCode.NotFound, "NoSuchBucket",
                new InvalidOperationException($"Bucket '{Name}' does not exist."));
    }
}

public class FakeObjectBucket<TMetadata, TKey>(string name, string? prefix, FakeObjectStore store)
    : FakeObjectBucket(name, store), IObjectBucket<TMetadata, TKey>
    where TMetadata : class, IObjectMetadata
    where TKey : notnull
{
    readonly Func<TKey, string> _keySerializer = ObjectKeySerializer.Get<TKey>();

    public Task<ObjectItem<TMetadata, TKey>?> FindOneAsync(TKey objectId, CancellationToken cancellationToken = default)
    {
        var obj = Store.GetObject(Name, GetKey(objectId));
        if (obj is null)
            return Task.FromResult<ObjectItem<TMetadata, TKey>?>(null);

        return Task.FromResult<ObjectItem<TMetadata, TKey>?>(CreateItem(objectId, obj, MaterializeMetadata(obj.Metadata)));
    }

    public Task<Stream?> OpenReadAsync(TKey objectId, CancellationToken cancellationToken = default)
    {
        var obj = Store.GetObject(Name, GetKey(objectId));
        if (obj is null)
            return Task.FromResult<Stream?>(null);

        return Task.FromResult<Stream?>(new MemoryStream(obj.Content, writable: false));
    }

    public async Task<ObjectItem<TMetadata, TKey>> UploadAsync(TKey objectId, TMetadata metadata, Stream content, UploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(content);

        // Production uploads fail with NoSuchBucket; a silently auto-created bucket would hide exactly the
        // kind of missing-provisioning bug the fake exists to catch. Find/Read/Delete stay lenient (null/false),
        // matching the real client.
        RequireBucket();

        UploadStreamGuard.ThrowIfConsumed(content);

        byte[] bytes;
        if (content.CanSeek)
        {
            bytes = new byte[content.Length - content.Position];
            await content.ReadExactlyAsync(bytes, cancellationToken);
        }
        else
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, cancellationToken);
            bytes = ms.ToArray();
        }

        var key = GetKey(objectId);

        // Snapshot the metadata: real S3 serializes it onto the wire, so reassigning the caller's
        // instance properties after the upload must not change what is stored.
        Store.PutObject(Name, key, bytes, MaterializeMetadata(metadata), options);

        // Like production, the returned item carries the caller's own metadata instance.
        return CreateItem(objectId, Store.GetObject(Name, key)!, metadata);
    }

    public Task<Uri> GetPresignedReadUrlAsync(TKey objectId, TimeSpan expiresIn, CancellationToken cancellationToken = default)
        => Task.FromResult(FakePresignedUrl(objectId, expiresIn, write: false, contentType: null));

    public Task<Uri> GetPresignedWriteUrlAsync(TKey objectId, TimeSpan expiresIn, string? contentType = null, CancellationToken cancellationToken = default)
        => Task.FromResult(FakePresignedUrl(objectId, expiresIn, write: true, contentType));

    // Deterministic fake URL carrying the bucket, the serialized key and the verb, so tests can assert them.
    Uri FakePresignedUrl(TKey objectId, TimeSpan expiresIn, bool write, string? contentType)
    {
        PresignedUrlLimits.Validate(expiresIn, nameof(expiresIn));

        // '/' stays a path separator, like in a real presigned URL; only the segments are escaped.
        var escapedKey = string.Join('/', GetKey(objectId).Split('/').Select(Uri.EscapeDataString));

        var url = $"https://fake.objectstorage.local/{Name}/{escapedKey}" +
            $"?verb={(write ? "PUT" : "GET")}&expires={(long)expiresIn.TotalSeconds}";
        if (contentType is not null)
            url += $"&content-type={Uri.EscapeDataString(contentType)}";

        return new Uri(url);
    }

    public Task<bool> DeleteOneAsync(TKey objectId, CancellationToken cancellationToken = default)
        => Task.FromResult(Store.DeleteObject(Name, GetKey(objectId)));

    // private protected: the parameter type is internal, and only the in-assembly Guid subclass overrides it.
    private protected virtual ObjectItem<TMetadata, TKey> CreateItem(TKey id, FakeStoredObject obj, TMetadata metadata)
        => new() { Id = id, Size = obj.Size, ETag = obj.ETag, Metadata = metadata };

    /// <summary>
    /// Materializes stored metadata as <typeparamref name="TMetadata"/>, mirroring production schema
    /// evolution for cross-type reads (two mappings on one destination): assignable properties copy over,
    /// string-convertible ones transfer through the same invariant-string codec production uses (a failed
    /// conversion throws, like production), and properties absent from the stored type stay at their
    /// defaults. Returns a fresh instance; the copy is top-level — reference-typed property values are
    /// shared with the source, so treat stored metadata as read-only.
    /// </summary>
    private protected static TMetadata MaterializeMetadata(object stored)
    {
        var result = Activator.CreateInstance<TMetadata>();

        foreach (var target in typeof(TMetadata).GetProperties())
        {
            if (!target.CanWrite)
                continue;

            var source = stored.GetType().GetProperty(target.Name);
            if (source is null || !source.CanRead)
                continue;

            var value = source.GetValue(stored);
            if (value is null)
                continue;

            if (target.PropertyType.IsAssignableFrom(source.PropertyType))
            {
                target.SetValue(result, value);
                continue;
            }

            // Cross-type property: production round-trips metadata through invariant strings, so a
            // convertible value (int -> string, int -> long, ...) must read back populated here too.
            var targetType = Nullable.GetUnderlyingType(target.PropertyType) ?? target.PropertyType;
            target.SetValue(result, MetadataValueConverter.FromInvariantString(
                MetadataValueConverter.ToInvariantString(value), targetType));
        }

        return result;
    }

    string GetKey(TKey objectId)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        return DestinationValidator.JoinKey(prefix, _keySerializer(objectId));
    }

    private protected sealed override string? BuildListPrefix(string? keyPrefix)
        => DestinationValidator.JoinListPrefix(prefix, keyPrefix);
}

/// <summary>Guid-keyed fake bucket — the historic shape with <see cref="ObjectItem{TMetadata}"/> results.</summary>
public class FakeObjectBucket<TMetadata>(string name, string? prefix, FakeObjectStore store)
    : FakeObjectBucket<TMetadata, Guid>(name, prefix, store), IObjectBucket<TMetadata>
    where TMetadata : class, IObjectMetadata
{
    private protected override ObjectItem<TMetadata, Guid> CreateItem(Guid id, FakeStoredObject obj, TMetadata metadata)
        => new ObjectItem<TMetadata> { Id = id, Size = obj.Size, ETag = obj.ETag, Metadata = metadata };

    public new async Task<ObjectItem<TMetadata>?> FindOneAsync(Guid objectId, CancellationToken cancellationToken = default)
        => (ObjectItem<TMetadata>?)await base.FindOneAsync(objectId, cancellationToken);

    public new async Task<ObjectItem<TMetadata>> UploadAsync(Guid objectId, TMetadata metadata, Stream content, UploadOptions? options = null, CancellationToken cancellationToken = default)
        => (ObjectItem<TMetadata>)await base.UploadAsync(objectId, metadata, content, options, cancellationToken);
}
