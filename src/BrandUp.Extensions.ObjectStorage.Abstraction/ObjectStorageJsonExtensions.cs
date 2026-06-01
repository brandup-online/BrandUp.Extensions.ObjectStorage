using System.Text.Json;

namespace BrandUp.Extensions.ObjectStorage;

public static class ObjectStorageJsonExtensions
{
    #region IObjectStorage

    public static async Task<TContent?> ReadJsonAsync<TMetadata, TContent>(
        this IObjectStorage storage,
        Guid objectId,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
        where TMetadata : class, IObjectMetadata
        where TContent : class
    {
        ArgumentNullException.ThrowIfNull(storage);

        await using var stream = await storage.ReadAsync<TMetadata>(objectId, cancellationToken);
        if (stream is null)
            return null;

        return await JsonSerializer.DeserializeAsync<TContent>(stream, options, cancellationToken);
    }

    public static async Task<ObjectItem<TMetadata>> UploadJsonAsync<TMetadata, TContent>(
        this IObjectStorage storage,
        Guid objectId,
        TMetadata metadata,
        TContent content,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
        where TMetadata : class, IObjectMetadata
        where TContent : class
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(content);

        using var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(stream, content, options, cancellationToken);
        stream.Seek(0, SeekOrigin.Begin);

        return await storage.UploadAsync(objectId, metadata, stream, cancellationToken);
    }

    #endregion

    #region IObjectBucket<TMetadata>

    public static async Task<TContent?> ReadJsonAsync<TMetadata, TContent>(
        this IObjectBucket<TMetadata> bucket,
        Guid objectId,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
        where TMetadata : class, IObjectMetadata
        where TContent : class
    {
        ArgumentNullException.ThrowIfNull(bucket);

        await using var stream = await bucket.OpenReadAsync(objectId, cancellationToken);
        if (stream is null)
            return null;

        return await JsonSerializer.DeserializeAsync<TContent>(stream, options, cancellationToken);
    }

    public static async Task<ObjectItem<TMetadata>> UploadJsonAsync<TMetadata, TContent>(
        this IObjectBucket<TMetadata> bucket,
        Guid objectId,
        TMetadata metadata,
        TContent content,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
        where TMetadata : class, IObjectMetadata
        where TContent : class
    {
        ArgumentNullException.ThrowIfNull(bucket);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(content);

        using var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(stream, content, options, cancellationToken);
        stream.Seek(0, SeekOrigin.Begin);

        return await bucket.UploadAsync(objectId, metadata, stream, cancellationToken);
    }

    #endregion
}
