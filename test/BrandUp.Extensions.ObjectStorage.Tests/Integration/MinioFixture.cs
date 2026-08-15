using Microsoft.Extensions.DependencyInjection;

namespace BrandUp.Extensions.ObjectStorage.Integration;

/// <summary>
/// Shared per-class fixture for MinIO integration tests. Builds an <see cref="IObjectStorageClient"/> /
/// <see cref="IObjectStorageContext"/> against the MinIO endpoint from the environment, and provisions a single
/// throwaway bucket (mapped to <see cref="TestFileMetadata"/>) that is dropped on teardown. When MinIO is
/// not configured initialization is a no-op — all tests are skipped via <see cref="MinioFactAttribute"/>.
/// </summary>
public sealed class MinioFixture : IAsyncLifetime
{
    ServiceProvider? _provider;

    /// <summary>
    /// Name of the throwaway bucket created for object-level tests; unique per test run. Uses only lowercase
    /// letters and digits (Guid "n" format) so it is valid both as an S3 bucket name and as an AddMapping
    /// destination (which allows only letters, digits and '/').
    /// </summary>
    public string BucketName { get; } = "it" + Guid.NewGuid().ToString("n");

    public IObjectStorageClient Client => _provider?.GetRequiredService<IObjectStorageClient>()
        ?? throw new InvalidOperationException("MinIO is not configured.");

    public IObjectStorageContext Storage => _provider?.GetRequiredService<IObjectStorageContext>()
        ?? throw new InvalidOperationException("MinIO is not configured.");

    /// <summary>Typed storage context over the same throwaway bucket, whose name it takes from configuration.</summary>
    public MinioStorageContext Context => _provider?.GetRequiredService<MinioStorageContext>()
        ?? throw new InvalidOperationException("MinIO is not configured.");

    public async ValueTask InitializeAsync()
    {
        if (!MinioEnvironment.IsConfigured)
            return;

        var services = new ServiceCollection();
        services.AddObjectStorage(MinioEnvironment.Apply)
          .AddMapping<TestFileMetadata>(BucketName)
          // Same destination on purpose: reading objects written as TestFileMetadata through the extended
          // type exercises the schema-evolution path (see MinioStorageTests).
          .AddMapping<ExtendedTestFileMetadata>(BucketName);

        services.AddObjectStorage<MinioStorageContext>(o =>
        {
            MinioEnvironment.Apply(o);
            // Bucket name is known only at run time; the object key prefix comes from configuration as well.
            o.Objects["Files"] = $"{BucketName}/ctx";
        })
        // Empty settings mean "this bucket is ours, create it with defaults".
        .ConfigureBucket<ContextFileMetadata>(_ => { });

        _provider = services.BuildServiceProvider();

        // Provisioning through the context: names come from configuration, so they are not repeated here.
        await Context.EnsureBucketsAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is null)
            return;

        try
        {
            await Client.DropBucketAsync(BucketName);
        }
        catch
        {
            // best-effort cleanup; a leaked test bucket must not fail the run
        }

        await _provider.DisposeAsync();
    }
}

/// <summary>Sample metadata used by object-level integration tests.</summary>
public class TestFileMetadata : IObjectMetadata
{
    public string? FileName { get; set; }
    public string? ContentType { get; set; }
}

/// <summary>
/// <see cref="TestFileMetadata"/> after schema evolution: extra properties (including value types) that
/// objects written earlier do not carry.
/// </summary>
public class ExtendedTestFileMetadata : IObjectMetadata
{
    public string? FileName { get; set; }
    public string? ContentType { get; set; }
    public int Version { get; set; }
    public DateTime ArchivedAt { get; set; }
}

/// <summary>Metadata of the context-based integration tests; separate type so both mappings coexist.</summary>
public class ContextFileMetadata : IObjectMetadata
{
    public string? FileName { get; set; }
}

/// <summary>
/// Storage context whose bucket is not declared in code: <c>[Bucket]</c> only names the configuration key
/// (here the property name), and the bucket itself comes from <see cref="ObjectStorageOptions.Objects"/>.
/// </summary>
public class MinioStorageContext : ObjectStorageContext
{
    [Bucket]
    public IObjectBucket<ContextFileMetadata> Files { get; private set; } = null!;
}
