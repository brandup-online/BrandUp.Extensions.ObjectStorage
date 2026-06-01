using Microsoft.Extensions.Options;

namespace BrandUp.Extensions.ObjectStorage.Internals;

internal class S3ObjectStorage : IObjectStorage
{
    readonly IS3Client _client;
    readonly Dictionary<Type, ObjectMapping> _mappings = [];

    public S3ObjectStorage(IS3Client client, IOptions<ObjectStorageMappingsOptions> mappingsOptions)
    {
        _client = client;

        foreach (var (type, destination) in mappingsOptions.Value.Destinations)
            _mappings[type] = ObjectMapping.Create(type, destination);
    }

    public async Task<ObjectItem<TMetadata>?> FindAsync<TMetadata>(Guid objectId, CancellationToken cancellationToken = default)
        where TMetadata : class, IObjectMetadata
    {
        var mapping = GetMapping<TMetadata>();
        var obj = await _client.FindAsync(mapping.BucketName, mapping.GetObjectKey(objectId), mapping.MetadataKeys, cancellationToken);
        if (obj is null)
            return null;

        return ToObjectItem<TMetadata>(objectId, obj, (TMetadata)mapping.Deserialize(obj.Metadata));
    }

    public Task<Stream?> ReadAsync<TMetadata>(Guid objectId, CancellationToken cancellationToken = default)
        where TMetadata : class, IObjectMetadata
    {
        var mapping = GetMapping<TMetadata>();
        return _client.ReadAsync(mapping.BucketName, mapping.GetObjectKey(objectId), cancellationToken);
    }

    public async Task<ObjectItem<TMetadata>> UploadAsync<TMetadata>(Guid objectId, TMetadata metadata, Stream stream, CancellationToken cancellationToken = default)
        where TMetadata : class, IObjectMetadata
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(stream);

        var mapping = GetMapping<TMetadata>();
        var serialized = mapping.Serialize(metadata);
        var obj = await _client.UploadAsync(mapping.BucketName, mapping.GetObjectKey(objectId), serialized, stream, cancellationToken);

        return ToObjectItem(objectId, obj, metadata);
    }

    public Task<bool> DeleteAsync<TMetadata>(Guid objectId, CancellationToken cancellationToken = default)
        where TMetadata : class, IObjectMetadata
    {
        var mapping = GetMapping<TMetadata>();
        return _client.DeleteAsync(mapping.BucketName, mapping.GetObjectKey(objectId), cancellationToken);
    }

    ObjectMapping GetMapping<TMetadata>() where TMetadata : class, IObjectMetadata
    {
        var type = typeof(TMetadata);
        if (!_mappings.TryGetValue(type, out var mapping))
            throw new InvalidOperationException($"No mapping configured for type {type.FullName}.");
        return mapping;
    }

    static ObjectItem<TMetadata> ToObjectItem<TMetadata>(Guid id, S3StorageObject obj, TMetadata metadata)
        where TMetadata : class, IObjectMetadata
        => new() { Id = id, Size = obj.Size, ETag = obj.Etag, Metadata = metadata };
}
