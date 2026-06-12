using BrandUp.Extensions.ObjectStorage.Internals;
using Microsoft.Extensions.DependencyInjection;

namespace BrandUp.Extensions.ObjectStorage;

public class ObjectStorageBuilder(IServiceCollection services)
{
    /// <summary>
    /// Registers a credentials provider supplying (typically temporary/STS) credentials. When registered,
    /// <see cref="ObjectStorageOptions.AccessKeyId"/>/<see cref="ObjectStorageOptions.SecretAccessKey"/> become
    /// optional, and the S3 client refreshes credentials in place without being recreated.
    /// </summary>
    public ObjectStorageBuilder UseCredentialsProvider<TProvider>()
        where TProvider : class, IObjectStorageCredentialsProvider
    {
        services.AddSingleton<IObjectStorageCredentialsProvider, TProvider>();
        services.AddSingleton<CredentialsProviderMarker>();
        return this;
    }

    /// <inheritdoc cref="UseCredentialsProvider{TProvider}()"/>
    public ObjectStorageBuilder UseCredentialsProvider(Func<IServiceProvider, IObjectStorageCredentialsProvider> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        services.AddSingleton(factory);
        services.AddSingleton<CredentialsProviderMarker>();
        return this;
    }

    public ObjectStorageBuilder AddMapping<TMetadata>(string destination)
        where TMetadata : class, IObjectMetadata
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        destination = destination.Trim().Trim('/');

        if (destination.Length == 0)
            throw new ArgumentException("Destination cannot be empty.", nameof(destination));

        foreach (var c in destination)
        {
            if (!char.IsLetterOrDigit(c) && c != '/')
                throw new ArgumentException($"Invalid character '{c}'. Only letters, digits, and '/' are allowed.", nameof(destination));
        }

        if (destination.Contains("//"))
            throw new ArgumentException("Destination contains consecutive '/' characters.", nameof(destination));

        services.Configure<ObjectStorageMappingsOptions>(opts =>
            opts.Destinations[typeof(TMetadata)] = destination);

        // Регистрация типизированного бакета для прямого внедрения через DI
        services.AddSingleton<IObjectBucket<TMetadata>>(sp =>
            sp.GetRequiredService<IObjectStorageClient>().GetBucket<TMetadata>());

        return this;
    }
}
