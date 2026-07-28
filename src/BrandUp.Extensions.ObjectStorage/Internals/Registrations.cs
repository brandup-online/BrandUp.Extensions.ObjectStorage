using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BrandUp.Extensions.ObjectStorage.Internals;

/// <summary>Registration of <c>IObjectBucket&lt;TMetadata&gt;</c> shared by the mapping API and storage contexts.</summary>
internal static class BucketRegistration
{
    public static void Register(
        IServiceCollection services,
        ObjectStorageRegistry registry,
        Type owner,
        Type metadataType,
        Func<IServiceProvider, object> resolve,
        Type? serviceType = null)
    {
        serviceType ??= typeof(IObjectBucket<>).MakeGenericType(metadataType);

        if (registry.ClaimMetadata(metadataType, owner, out var currentOwner))
        {
            services.AddSingleton(serviceType, resolve);
            return;
        }

        // Two owners map the same metadata type, so a bare bucket injection would be ambiguous. Fail with an
        // explanation at resolve time instead of silently binding to one of them.
        var message = MetadataOwners.AmbiguityMessage(metadataType,
            ObjectStorageRegistry.Display(currentOwner), ObjectStorageRegistry.Display(owner));

        services.AddSingleton(serviceType, _ => throw new InvalidOperationException(message));
    }
}

/// <summary>Registration of a credentials provider for one connection.</summary>
internal static class CredentialsRegistration
{
    public static void Add<TProvider>(IServiceCollection services, ObjectStorageRegistry registry, string connectionName)
        where TProvider : class, IObjectStorageCredentialsProvider
    {
        services.AddKeyedSingleton<IObjectStorageCredentialsProvider, TProvider>(connectionName);
        Complete(services, registry, connectionName);
    }

    public static void Add(
        IServiceCollection services,
        ObjectStorageRegistry registry,
        string connectionName,
        Func<IServiceProvider, IObjectStorageCredentialsProvider> factory)
    {
        services.AddKeyedSingleton<IObjectStorageCredentialsProvider>(connectionName, (sp, _) => factory(sp));
        Complete(services, registry, connectionName);
    }

    static void Complete(IServiceCollection services, ObjectStorageRegistry registry, string connectionName)
    {
        registry.MarkCredentialsProvider(connectionName);

        if (connectionName.Length != 0)
            return;

        // Default connection keeps its pre-existing surface: unkeyed provider resolution and the validator marker.
        services.TryAddSingleton<IObjectStorageCredentialsProvider>(
            sp => sp.GetRequiredKeyedService<IObjectStorageCredentialsProvider>(string.Empty));
        services.TryAddSingleton<CredentialsProviderMarker>();
    }
}
