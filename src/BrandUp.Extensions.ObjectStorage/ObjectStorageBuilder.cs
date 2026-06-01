using BrandUp.Extensions.ObjectStorage.Internals;
using Microsoft.Extensions.DependencyInjection;

namespace BrandUp.Extensions.ObjectStorage;

public class ObjectStorageBuilder(IServiceCollection services)
{
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
        return this;
    }
}
