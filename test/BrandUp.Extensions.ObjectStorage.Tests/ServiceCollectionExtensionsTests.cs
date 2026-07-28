using Microsoft.Extensions.DependencyInjection;

namespace BrandUp.Extensions.ObjectStorage;

public class ServiceCollectionExtensionsTests
{
    static IServiceProvider BuildProvider(Action<ObjectStorageBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        var builder = services.AddObjectStorage(opts =>
        {
            opts.ServiceUrl           = "https://storage.example.com";
            opts.AuthenticationRegion = "us-east-1";
            opts.AccessKeyId          = "key";
            opts.SecretAccessKey      = "secret";
        });
        configure?.Invoke(builder);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddObjectStorage_RegistersIObjectStorage()
    {
        var sp = BuildProvider();
        Assert.NotNull(sp.GetService<IObjectStorageContext>());
    }

    [Fact]
    public void AddObjectStorage_RegistersIObjectStorageClient()
    {
        var sp = BuildProvider();
        Assert.NotNull(sp.GetService<IObjectStorageClient>());
    }

    [Fact]
    public void AddMapping_RegistersTypedBucket()
    {
        var sp = BuildProvider(b => b.AddMapping<TestMetadata>("bucket/items"));
        Assert.NotNull(sp.GetService<IObjectBucket<TestMetadata>>());
    }

    [Fact]
    public void AddObjectStorage_ThrowsForNullServices()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ((IServiceCollection)null!).AddObjectStorage(_ => { }));
    }

    private class TestMetadata : IObjectMetadata { }
}
