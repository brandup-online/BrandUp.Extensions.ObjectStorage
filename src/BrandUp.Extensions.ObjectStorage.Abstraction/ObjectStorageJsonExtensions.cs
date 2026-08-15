using System.Text.Json;

namespace BrandUp.Extensions.ObjectStorage;

public static class ObjectStorageJsonExtensions
{
    #region IObjectStorageContext

    public static async Task<TContent?> ReadJsonAsync<TMetadata, TContent>(
        this IObjectStorageContext storage,
        Guid objectId,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
        where TMetadata : class, IObjectMetadata
        where TContent : class
    {
        ArgumentNullException.ThrowIfNull(storage);

        return await DeserializeAsync<TContent>(
            await storage.ReadAsync<TMetadata>(objectId, cancellationToken), options, cancellationToken);
    }

    public static async Task<ObjectItem<TMetadata>> UploadJsonAsync<TMetadata, TContent>(
        this IObjectStorageContext storage,
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

        using var stream = await SerializeAsync(content, options, cancellationToken);
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

        return await DeserializeAsync<TContent>(
            await bucket.OpenReadAsync(objectId, cancellationToken), options, cancellationToken);
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

        using var stream = await SerializeAsync(content, options, cancellationToken);
        return await bucket.UploadAsync(objectId, metadata, stream, JsonUploadOptions, cancellationToken);
    }

    #endregion

    #region IObjectBucket<TMetadata, TKey>

    public static async Task<TContent?> ReadJsonAsync<TMetadata, TKey, TContent>(
        this IObjectBucket<TMetadata, TKey> bucket,
        TKey objectId,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
        where TMetadata : class, IObjectMetadata
        where TKey : notnull
        where TContent : class
    {
        ArgumentNullException.ThrowIfNull(bucket);

        return await DeserializeAsync<TContent>(
            await bucket.OpenReadAsync(objectId, cancellationToken), options, cancellationToken);
    }

    public static async Task<ObjectItem<TMetadata, TKey>> UploadJsonAsync<TMetadata, TKey, TContent>(
        this IObjectBucket<TMetadata, TKey> bucket,
        TKey objectId,
        TMetadata metadata,
        TContent content,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
        where TMetadata : class, IObjectMetadata
        where TKey : notnull
        where TContent : class
    {
        ArgumentNullException.ThrowIfNull(bucket);
        ArgumentNullException.ThrowIfNull(metadata);

        using var stream = await SerializeAsync(content, options, cancellationToken);
        return await bucket.UploadAsync(objectId, metadata, stream, JsonUploadOptions, cancellationToken);
    }

    #endregion

    // JSON uploads through a bucket are served back as JSON; the IObjectStorageContext facade has no
    // options channel and keeps the historic behavior.
    static readonly UploadOptions JsonUploadOptions = new() { ContentType = "application/json" };

    static async Task<TContent?> DeserializeAsync<TContent>(
        Stream? stream, JsonSerializerOptions? options, CancellationToken cancellationToken)
        where TContent : class
    {
        if (stream is null)
            return null;

        await using (stream)
            return await JsonSerializer.DeserializeAsync<TContent>(stream, options, cancellationToken);
    }

    static async Task<MemoryStream> SerializeAsync<TContent>(
        TContent content, JsonSerializerOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var stream = new MemoryStream();
        await JsonSerializer.SerializeAsync(stream, content, options, cancellationToken);
        stream.Seek(0, SeekOrigin.Begin);

        return stream;
    }
}
