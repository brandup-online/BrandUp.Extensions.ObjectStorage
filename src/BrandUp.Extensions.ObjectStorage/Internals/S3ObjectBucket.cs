namespace BrandUp.Extensions.ObjectStorage.Internals;

internal class S3ObjectBucket(string name, IS3Client client) : IObjectBucket
{
    /// <summary>Connection this bucket talks to; compared by reference to decide whether a server-side copy is possible.</summary>
    internal IS3Client Client { get; } = client;

    public string Name { get; } = name;

    public Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
        => Client.BucketExistsAsync(Name, cancellationToken);

    public IAsyncEnumerable<ObjectListItem> ListAsync(string? keyPrefix = null, CancellationToken cancellationToken = default)
        => Client.ListObjectsAsync(Name, BuildListPrefix(keyPrefix), cancellationToken);

    /// <summary>A typed bucket narrows the listing to its own mapping prefix.</summary>
    private protected virtual string? BuildListPrefix(string? keyPrefix) => keyPrefix;

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

        var current = await GetSettingsAsync(cancellationToken);
        var desired = new BucketSettings
        {
            Versioning = current.Versioning,
            Access = current.Access,
            LifecycleRules = [.. current.LifecycleRules]
        };
        configure(desired);

        await WriteChangedAsync(Client, Name, current, desired, cancellationToken);
    }

    /// <summary>
    /// Writes only the settings that differ between <paramref name="current"/> and <paramref name="desired"/>.
    /// Untouched aspects cost no requests — and providers that do not support one of them (MinIO rejects
    /// bucket ACL grants) are not hit unless the caller actually changes it.
    /// </summary>
    internal static Task WriteChangedAsync(
        IS3Client client, string bucketName, BucketSettings current, BucketSettings desired, CancellationToken cancellationToken)
    {
        var writes = new List<Task>(3);

        if (desired.Versioning != current.Versioning)
            writes.Add(client.SetVersioningAsync(bucketName, desired.Versioning, cancellationToken));

        if (desired.Access != current.Access)
            writes.Add(client.SetAccessAsync(bucketName, desired.Access, cancellationToken));

        if (!desired.LifecycleRules.SequenceEqual(current.LifecycleRules))
            writes.Add(client.SetLifecycleAsync(bucketName, desired.LifecycleRules, cancellationToken));

        return writes.Count > 0 ? Task.WhenAll(writes) : Task.CompletedTask;
    }
}

internal class S3ObjectBucket<TMetadata, TKey>(IS3Client client, ObjectMapping mapping)
    : S3ObjectBucket(mapping.BucketName, client), IObjectBucket<TMetadata, TKey>
    where TMetadata : class, IObjectMetadata
    where TKey : notnull
{
    readonly Func<TKey, string> _keySerializer = ObjectKeySerializer.Get<TKey>();

    public async Task<ObjectItem<TMetadata, TKey>?> FindOneAsync(TKey objectId, CancellationToken cancellationToken = default)
    {
        var obj = await Client.FindAsync(Name, GetObjectKey(objectId), mapping.MetadataKeys, cancellationToken);
        if (obj is null)
            return null;

        return CreateItem(objectId, obj, (TMetadata)mapping.Deserialize(obj.Metadata));
    }

    public Task<Stream?> OpenReadAsync(TKey objectId, CancellationToken cancellationToken = default)
        => Client.ReadAsync(Name, GetObjectKey(objectId), cancellationToken);

    public async Task<ObjectItem<TMetadata, TKey>> UploadAsync(TKey objectId, TMetadata metadata, Stream content, UploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(content);

        var serialized = mapping.Serialize(metadata);
        var obj = await Client.UploadAsync(Name, GetObjectKey(objectId), serialized, content, options, cancellationToken);

        return CreateItem(objectId, obj, metadata);
    }

    public Task<bool> DeleteOneAsync(TKey objectId, CancellationToken cancellationToken = default)
        => Client.DeleteAsync(Name, GetObjectKey(objectId), cancellationToken);

    public Task<bool> CopyToAsync<TTargetMetadata, TTargetKey>(
        TKey objectId,
        IObjectBucket<TTargetMetadata, TTargetKey> target,
        TTargetKey targetObjectId,
        TTargetMetadata targetMetadata,
        UploadOptions? options = null,
        CancellationToken cancellationToken = default)
        where TTargetMetadata : class, IObjectMetadata
        where TTargetKey : notnull
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(targetMetadata);

        // CopyObject is one request inside one account, so a bucket of another connection is out of reach.
        // Falling back to read+upload would quietly do the very thing this method exists to avoid.
        if (target is not S3ObjectBucket<TTargetMetadata, TTargetKey> s3Target || !ReferenceEquals(s3Target.Client, Client))
            throw CopyTarget.Mismatch(this, target);

        // Key and metadata are composed by the target's own mapping — its prefix, its key serializer.
        return Client.CopyAsync(
            Name, GetObjectKey(objectId),
            s3Target.Name, s3Target.GetObjectKey(targetObjectId), s3Target.SerializeMetadata(targetMetadata),
            options, cancellationToken);
    }

    public Task<Uri> GetPresignedReadUrlAsync(TKey objectId, TimeSpan expiresIn, CancellationToken cancellationToken = default)
        => Client.GetPresignedUrlAsync(Name, GetObjectKey(objectId), expiresIn, forWrite: false, contentType: null, cancellationToken);

    public Task<Uri> GetPresignedWriteUrlAsync(TKey objectId, TimeSpan expiresIn, string? contentType = null, CancellationToken cancellationToken = default)
        => Client.GetPresignedUrlAsync(Name, GetObjectKey(objectId), expiresIn, forWrite: true, contentType, cancellationToken);

    /// <summary>The Guid subclass narrows the item to <see cref="ObjectItem{TMetadata}"/>.</summary>
    protected virtual ObjectItem<TMetadata, TKey> CreateItem(TKey id, S3StorageObject obj, TMetadata metadata)
        => new() { Id = id, Size = obj.Size, ETag = obj.Etag, Metadata = metadata };

    private protected sealed override string? BuildListPrefix(string? keyPrefix)
        => DestinationValidator.JoinListPrefix(mapping.ObjectKeyPrefix, keyPrefix);

    // internal: a copy composes the key and the metadata through the target bucket, which is a different
    // generic instantiation and cannot reach private members.
    internal string GetObjectKey(TKey objectId)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        return mapping.GetObjectKey(_keySerializer(objectId));
    }

    internal IDictionary<string, string> SerializeMetadata(TMetadata metadata)
        => mapping.Serialize(metadata);
}

/// <summary>Guid-keyed bucket, with <see cref="ObjectItem{TMetadata}"/> results.</summary>
internal sealed class S3ObjectBucket<TMetadata>(IS3Client client, ObjectMapping mapping)
    : S3ObjectBucket<TMetadata, Guid>(client, mapping), IObjectBucket<TMetadata>
    where TMetadata : class, IObjectMetadata
{
    protected override ObjectItem<TMetadata, Guid> CreateItem(Guid id, S3StorageObject obj, TMetadata metadata)
        => new ObjectItem<TMetadata> { Id = id, Size = obj.Size, ETag = obj.Etag, Metadata = metadata };

    public new async Task<ObjectItem<TMetadata>?> FindOneAsync(Guid objectId, CancellationToken cancellationToken = default)
        => (ObjectItem<TMetadata>?)await base.FindOneAsync(objectId, cancellationToken);

    public new async Task<ObjectItem<TMetadata>> UploadAsync(Guid objectId, TMetadata metadata, Stream content, UploadOptions? options = null, CancellationToken cancellationToken = default)
        => (ObjectItem<TMetadata>)await base.UploadAsync(objectId, metadata, content, options, cancellationToken);
}
