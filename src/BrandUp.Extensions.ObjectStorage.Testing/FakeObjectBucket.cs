namespace BrandUp.Extensions.ObjectStorage;

public class FakeObjectBucket(string name, FakeObjectStore store) : IObjectBucket
{
    protected readonly FakeObjectStore Store = store;

    public string Name { get; } = name;

    public Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Store.BucketExists(Name));

    public Task<BucketSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Store.GetSettings(Name));

    public Task UpdateSettingsAsync(Action<BucketSettings> configure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Store.UpdateSettings(Name, configure);
        return Task.CompletedTask;
    }
}

public class FakeObjectBucket<TMetadata>(string name, string? prefix, FakeObjectStore store)
    : FakeObjectBucket(name, store), IObjectBucket<TMetadata>
    where TMetadata : class, IObjectMetadata
{
    public Task<ObjectItem<TMetadata>?> FindOneAsync(Guid objectId, CancellationToken cancellationToken = default)
    {
        var obj = Store.GetObject(Name, GetKey(objectId));
        if (obj is null)
            return Task.FromResult<ObjectItem<TMetadata>?>(null);

        return Task.FromResult<ObjectItem<TMetadata>?>(ToItem(objectId, obj));
    }

    public Task<Stream?> OpenReadAsync(Guid objectId, CancellationToken cancellationToken = default)
    {
        var obj = Store.GetObject(Name, GetKey(objectId));
        if (obj is null)
            return Task.FromResult<Stream?>(null);

        return Task.FromResult<Stream?>(new MemoryStream(obj.Content, writable: false));
    }

    public async Task<ObjectItem<TMetadata>> UploadAsync(Guid objectId, TMetadata metadata, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(content);

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

        Store.PutObject(Name, GetKey(objectId), bytes, metadata);

        return ToItem(objectId, Store.GetObject(Name, GetKey(objectId))!);
    }

    public Task<bool> DeleteOneAsync(Guid objectId, CancellationToken cancellationToken = default)
        => Task.FromResult(Store.DeleteObject(Name, GetKey(objectId)));

    string GetKey(Guid objectId)
    {
        var id = objectId.ToString("d");
        return prefix is null ? id : $"{prefix}_{id}";
    }

    static ObjectItem<TMetadata> ToItem(Guid id, FakeStoredObject obj)
        => new() { Id = id, Size = obj.Size, ETag = obj.ETag, Metadata = (TMetadata)obj.Metadata };
}
