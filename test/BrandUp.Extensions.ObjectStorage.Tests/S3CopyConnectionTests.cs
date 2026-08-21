using Microsoft.Extensions.DependencyInjection;

namespace BrandUp.Extensions.ObjectStorage;

/// <summary>
/// The same-connection guard of <c>CopyToAsync</c>. It runs before any request, so these need no server.
/// </summary>
public class S3CopyConnectionTests
{
    static Action<ObjectStorageOptions> Options(params (string Key, string Destination)[] objects) => o =>
    {
        o.ServiceUrl = "https://s3.example.com";   // never reached: the guard runs before any request
        o.AuthenticationRegion = "us-east-1";
        o.AccessKeyId = "key";
        o.SecretAccessKey = "secret";

        foreach (var (key, destination) in objects)
            o.Objects[key] = destination;
    };

    [Fact]
    public async Task CopyToAsync_TargetOfAnotherConnection_Throws()
    {
        // Two connections against the same endpoint are still two connections: a CopyObject cannot span them.
        var first = new ServiceCollection();
        first.AddObjectStorage(Options()).AddMapping<PhotoMetadata>("photos");

        var second = new ServiceCollection();
        second.AddObjectStorage(Options()).AddMapping<ArchiveMetadata>("archive");

        await using var firstProvider = first.BuildServiceProvider();
        await using var secondProvider = second.BuildServiceProvider();

        var source = firstProvider.GetRequiredService<IObjectStorageClient>().GetBucket<PhotoMetadata>();
        var target = secondProvider.GetRequiredService<IObjectStorageClient>().GetBucket<ArchiveMetadata>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.CopyToAsync(Guid.NewGuid(), target, Guid.NewGuid(), new ArchiveMetadata()));

        Assert.Contains("same storage connection", ex.Message);
    }

    [Fact]
    public async Task CopyToAsync_TargetOfAnotherContext_Throws()
    {
        // Each AddObjectStorage<TContext> gets its own connection, so a copy between two contexts is refused
        // even when both point at the same account. Give them one named connection to copy between them.
        var services = new ServiceCollection();
        services.AddObjectStorage<PhotoStorage>(Options(("Photos", "photos")));
        services.AddObjectStorage<ArchiveStorage>(Options(("Archive", "archive")));

        await using var provider = services.BuildServiceProvider();
        var photos = provider.GetRequiredService<PhotoStorage>();
        var archive = provider.GetRequiredService<ArchiveStorage>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => photos.Photos.CopyToAsync(Guid.NewGuid(), archive.Archive, Guid.NewGuid(), new ArchiveMetadata()));
    }

    public class PhotoMetadata : IObjectMetadata { }
    public class ArchiveMetadata : IObjectMetadata { }

    public class PhotoStorage : ObjectStorageContext
    {
        [Bucket]
        public IObjectBucket<PhotoMetadata> Photos { get; private set; } = null!;
    }

    public class ArchiveStorage : ObjectStorageContext
    {
        [Bucket]
        public IObjectBucket<ArchiveMetadata> Archive { get; private set; } = null!;
    }
}
