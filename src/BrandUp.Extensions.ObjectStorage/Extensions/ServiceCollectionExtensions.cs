using BrandUp.Extensions.ObjectStorage.Internals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BrandUp.Extensions.ObjectStorage;

public static class ServiceCollectionExtensions
{
    public static ObjectStorageBuilder AddObjectStorage(this IServiceCollection services, Action<ObjectStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<ObjectStorageOptions>().Configure(configure).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ObjectStorageOptions>, ObjectStorageOptionsValidator>();
        services.AddSingleton<IS3Client, S3Client>();
        services.AddSingleton<IObjectStorageClient, S3ObjectStorageClient>();
        services.AddSingleton<IObjectStorage, S3ObjectStorage>();

        return new ObjectStorageBuilder(services);
    }
}
