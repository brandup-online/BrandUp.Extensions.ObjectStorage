namespace BrandUp.Extensions.ObjectStorage;

public interface IObjectBucket
{
    string Name { get; }

    Task<bool> ExistsAsync(CancellationToken cancellationToken = default);
    Task<BucketSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task UpdateSettingsAsync(Action<BucketSettings> configure, CancellationToken cancellationToken = default);
}

public interface IObjectBucket<TMetadata> : IObjectBucket
    where TMetadata : class, IObjectMetadata
{
    Task<ObjectItem<TMetadata>?> FindOneAsync(Guid objectId, CancellationToken cancellationToken = default);
    Task<Stream?> OpenReadAsync(Guid objectId, CancellationToken cancellationToken = default);
    Task<ObjectItem<TMetadata>> UploadAsync(Guid objectId, TMetadata metadata, Stream content, CancellationToken cancellationToken = default);
    Task<bool> DeleteOneAsync(Guid objectId, CancellationToken cancellationToken = default);
}
