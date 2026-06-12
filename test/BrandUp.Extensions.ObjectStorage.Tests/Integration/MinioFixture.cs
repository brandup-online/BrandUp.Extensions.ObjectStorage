using Microsoft.Extensions.DependencyInjection;

namespace BrandUp.Extensions.ObjectStorage.Integration;

/// <summary>
/// Shared per-class fixture for MinIO integration tests. Builds an <see cref="IObjectStorageClient"/> /
/// <see cref="IObjectStorage"/> against the MinIO endpoint from the environment, and provisions a single
/// throwaway bucket (mapped to <see cref="TestFileMetadata"/>) that is dropped on teardown. When MinIO is
/// not configured initialization is a no-op — all tests are skipped via <see cref="MinioFactAttribute"/>.
/// </summary>
public sealed class MinioFixture : IAsyncLifetime
{
    ServiceProvider? _provider;

    /// <summary>Name of the throwaway bucket created for object-level tests; unique per test run.</summary>
    public string BucketName { get; } = "it-" + Guid.NewGuid().ToString("n");

    public IObjectStorageClient Client => _provider?.GetRequiredService<IObjectStorageClient>()
        ?? throw new InvalidOperationException("MinIO is not configured.");

    public IObjectStorage Storage => _provider?.GetRequiredService<IObjectStorage>()
        ?? throw new InvalidOperationException("MinIO is not configured.");

    public async Task InitializeAsync()
    {
        if (!MinioEnvironment.IsConfigured)
            return;

        var services = new ServiceCollection();
        services.AddObjectStorage(o =>
        {
            o.ServiceUrl = MinioEnvironment.ServiceUrl;
            o.AuthenticationRegion = MinioEnvironment.Region;
            o.AccessKeyId = MinioEnvironment.AccessKey;
            o.SecretAccessKey = MinioEnvironment.SecretKey;
            o.ForcePathStyle = true; // MinIO does not support virtual-hosted-style addressing
        }).AddMapping<TestFileMetadata>(BucketName);

        _provider = services.BuildServiceProvider();

        await Client.CreateBucketAsync(BucketName);
    }

    public async Task DisposeAsync()
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
