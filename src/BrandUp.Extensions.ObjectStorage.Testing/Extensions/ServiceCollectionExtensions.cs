using Microsoft.Extensions.DependencyInjection;

namespace BrandUp.Extensions.ObjectStorage;

public static class FakeObjectStorageServiceCollectionExtensions
{
    public static FakeObjectStorageBuilder AddFakeObjectStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var store = new FakeObjectStore();
        var client = new FakeObjectStorageClient(store);
        var storage = new FakeObjectStorage(client);

        services.AddSingleton(store);
        services.AddSingleton<IObjectStorageClient>(client);
        services.AddSingleton<IObjectStorage>(storage);

        return new FakeObjectStorageBuilder(services, store, client);
    }
}

public class FakeObjectStorageBuilder(IServiceCollection services, FakeObjectStore store, FakeObjectStorageClient client)
{
    public FakeObjectStorageBuilder AddMapping<TMetadata>(string destination)
        where TMetadata : class, IObjectMetadata
    {
        client.AddMapping<TMetadata>(destination);

        services.AddSingleton<IObjectBucket<TMetadata>>(
            _ => client.GetBucket<TMetadata>());

        return this;
    }

    /// <summary>
    /// Предзаполняет бакет перед тестами.
    /// </summary>
    public FakeObjectStorageBuilder WithBucket(string bucketName, Action<BucketSettings>? configure = null)
    {
        var settings = new BucketSettings();
        configure?.Invoke(settings);
        store.CreateBucket(bucketName.ToLower(), settings);
        return this;
    }
}
