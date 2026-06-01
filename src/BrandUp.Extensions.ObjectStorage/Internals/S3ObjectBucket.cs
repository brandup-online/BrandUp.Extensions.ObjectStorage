namespace BrandUp.Extensions.ObjectStorage.Internals;

internal class S3ObjectBucket(string name, IS3Client client) : IObjectBucket
{
    protected readonly IS3Client Client = client;

    public string Name { get; } = name;

    public Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
        => Client.BucketExistsAsync(Name, cancellationToken);

    public async Task<BucketSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var versioningTask = Client.GetVersioningAsync(Name, cancellationToken);
        var accessTask = Client.GetAccessAsync(Name, cancellationToken);
        var lifecycleTask = Client.GetLifecycleAsync(Name, cancellationToken);

        await Task.WhenAll(versioningTask, accessTask, lifecycleTask);

        return new BucketSettings
        {
            Versioning = versioningTask.Result,
            Access = accessTask.Result,
            LifecycleRules = lifecycleTask.Result.ToList()
        };
    }

    public async Task UpdateSettingsAsync(Action<BucketSettings> configure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var settings = await GetSettingsAsync(cancellationToken);
        configure(settings);

        await Task.WhenAll(
            Client.SetVersioningAsync(Name, settings.Versioning, cancellationToken),
            Client.SetAccessAsync(Name, settings.Access, cancellationToken),
            Client.SetLifecycleAsync(Name, settings.LifecycleRules, cancellationToken));
    }
}

internal class S3ObjectBucket<TMetadata>(IS3Client client, ObjectMapping mapping)
    : S3ObjectBucket(mapping.BucketName, client), IObjectBucket<TMetadata>
    where TMetadata : class, IObjectMetadata
{
    public async Task<ObjectItem<TMetadata>?> FindOneAsync(Guid objectId, CancellationToken cancellationToken = default)
    {
        var obj = await Client.FindAsync(Name, mapping.GetObjectKey(objectId), mapping.MetadataKeys, cancellationToken);
        if (obj is null)
            return null;

        return ToObjectItem(objectId, obj, (TMetadata)mapping.Deserialize(obj.Metadata));
    }

    public Task<Stream?> OpenReadAsync(Guid objectId, CancellationToken cancellationToken = default)
        => Client.ReadAsync(Name, mapping.GetObjectKey(objectId), cancellationToken);

    public async Task<ObjectItem<TMetadata>> UploadAsync(Guid objectId, TMetadata metadata, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(content);

        var serialized = mapping.Serialize(metadata);
        var obj = await Client.UploadAsync(Name, mapping.GetObjectKey(objectId), serialized, content, cancellationToken);

        return ToObjectItem(objectId, obj, metadata);
    }

    public Task<bool> DeleteOneAsync(Guid objectId, CancellationToken cancellationToken = default)
        => Client.DeleteAsync(Name, mapping.GetObjectKey(objectId), cancellationToken);

    static ObjectItem<TMetadata> ToObjectItem(Guid id, S3StorageObject obj, TMetadata metadata)
        => new() { Id = id, Size = obj.Size, ETag = obj.Etag, Metadata = metadata };
}
