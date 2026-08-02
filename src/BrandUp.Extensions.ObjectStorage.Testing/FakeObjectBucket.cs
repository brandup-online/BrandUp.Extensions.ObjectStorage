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
        // Production listing of a missing bucket is 404 NoSuchBucket.
        RequireBucket();

        foreach (var item in Store.ListObjects(Name, BuildListPrefix(keyPrefix)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }

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

        return Task.FromResult<ObjectItem<TMetadata, TKey>?>(CreateItem(objectId, obj));
    }

    public Task<Stream?> OpenReadAsync(TKey objectId, CancellationToken cancellationToken = default)
    {
        var obj = Store.GetObject(Name, GetKey(objectId));
        if (obj is null)
            return Task.FromResult<Stream?>(null);

        return Task.FromResult<Stream?>(new MemoryStream(obj.Content, writable: false));
    }

    public Task<ObjectItem<TMetadata, TKey>> UploadAsync(TKey objectId, TMetadata metadata, Stream content, CancellationToken cancellationToken = default)
        => UploadAsync(objectId, metadata, content, options: null, cancellationToken);

    public async Task<ObjectItem<TMetadata, TKey>> UploadAsync(TKey objectId, TMetadata metadata, Stream content, UploadOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(content);

        // Production uploads fail with NoSuchBucket; a silently auto-created bucket would hide exactly the
        // kind of missing-provisioning bug the fake exists to catch. Find/Read/Delete stay lenient (null/false),
        // matching the real client.
        RequireBucket();

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
        Store.PutObject(Name, key, bytes, metadata, options);

        return CreateItem(objectId, Store.GetObject(Name, key)!);
    }

    public Task<Uri> GetPresignedReadUrlAsync(TKey objectId, TimeSpan expiresIn, CancellationToken cancellationToken = default)
        => Task.FromResult(FakePresignedUrl(objectId, expiresIn, write: false, contentType: null));

    public Task<Uri> GetPresignedWriteUrlAsync(TKey objectId, TimeSpan expiresIn, string? contentType = null, CancellationToken cancellationToken = default)
        => Task.FromResult(FakePresignedUrl(objectId, expiresIn, write: true, contentType));

    // Deterministic fake URL carrying the bucket, the serialized key and the verb, so tests can assert them.
    Uri FakePresignedUrl(TKey objectId, TimeSpan expiresIn, bool write, string? contentType)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiresIn, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(expiresIn, PresignedUrlLimits.MaxLifetime);

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
    private protected virtual ObjectItem<TMetadata, TKey> CreateItem(TKey id, FakeStoredObject obj)
        => new() { Id = id, Size = obj.Size, ETag = obj.ETag, Metadata = MaterializeMetadata(obj.Metadata) };

    /// <summary>
    /// Mirrors production schema evolution for cross-type reads (two mappings on one destination):
    /// matching properties copy over, everything else stays at its default — instead of an
    /// InvalidCastException that production would never throw.
    /// </summary>
    private protected static TMetadata MaterializeMetadata(object stored)
    {
        if (stored is TMetadata same)
            return same;

        var result = Activator.CreateInstance<TMetadata>();

        foreach (var target in typeof(TMetadata).GetProperties())
        {
            if (!target.CanWrite)
                continue;

            var source = stored.GetType().GetProperty(target.Name);
            if (source is null || !source.CanRead || !target.PropertyType.IsAssignableFrom(source.PropertyType))
                continue;

            target.SetValue(result, source.GetValue(stored));
        }

        return result;
    }

    string GetKey(TKey objectId)
    {
        ArgumentNullException.ThrowIfNull(objectId);

        var id = _keySerializer(objectId);
        return prefix is null ? id : $"{prefix}{DestinationValidator.ObjectKeyPrefixDelimiter}{id}";
    }

    private protected sealed override string? BuildListPrefix(string? keyPrefix)
        => prefix is null ? keyPrefix : $"{prefix}{DestinationValidator.ObjectKeyPrefixDelimiter}{keyPrefix}";
}

/// <summary>Guid-keyed fake bucket — the historic shape with <see cref="ObjectItem{TMetadata}"/> results.</summary>
public class FakeObjectBucket<TMetadata>(string name, string? prefix, FakeObjectStore store)
    : FakeObjectBucket<TMetadata, Guid>(name, prefix, store), IObjectBucket<TMetadata>
    where TMetadata : class, IObjectMetadata
{
    private protected override ObjectItem<TMetadata, Guid> CreateItem(Guid id, FakeStoredObject obj)
        => new ObjectItem<TMetadata> { Id = id, Size = obj.Size, ETag = obj.ETag, Metadata = MaterializeMetadata(obj.Metadata) };

    public new async Task<ObjectItem<TMetadata>?> FindOneAsync(Guid objectId, CancellationToken cancellationToken = default)
        => (ObjectItem<TMetadata>?)await base.FindOneAsync(objectId, cancellationToken);

    public new async Task<ObjectItem<TMetadata>> UploadAsync(Guid objectId, TMetadata metadata, Stream content, CancellationToken cancellationToken = default)
        => (ObjectItem<TMetadata>)await base.UploadAsync(objectId, metadata, content, cancellationToken);

    public new async Task<ObjectItem<TMetadata>> UploadAsync(Guid objectId, TMetadata metadata, Stream content, UploadOptions? options, CancellationToken cancellationToken = default)
        => (ObjectItem<TMetadata>)await base.UploadAsync(objectId, metadata, content, options, cancellationToken);
}
