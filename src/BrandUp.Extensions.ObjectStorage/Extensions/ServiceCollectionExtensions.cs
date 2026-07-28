using BrandUp.Extensions.ObjectStorage.Internals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BrandUp.Extensions.ObjectStorage;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the default connection together with the unnamed <see cref="IObjectStorageClient"/> and
    /// <see cref="IObjectStorageContext"/>. Map metadata types with <see cref="ObjectStorageBuilder.AddMapping{TMetadata}(string)"/>.
    /// </summary>
    public static ObjectStorageBuilder AddObjectStorage(this IServiceCollection services, Action<ObjectStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var registry = AddConnectionCore(services, Options.DefaultName, configure);

        services.TryAddSingleton<IObjectStorageClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptionsMonitor<ObjectStorageOptions>>().Get(Options.DefaultName);
            var mappings = sp.GetRequiredService<IOptions<ObjectStorageMappingsOptions>>().Value.Destinations;

            // Physical bucket names come from the connection configuration; the mapping only declares them.
            var destinations = mappings.ToDictionary(
                m => m.Key,
                m => BucketResolver.ResolveDestination(
                    m.Value, options.Objects, options.BucketNamePrefix, options.BucketNameSuffix, m.Key.Name));

            return new S3ObjectStorageClient(sp.GetRequiredService<IS3ClientFactory>().Get(Options.DefaultName), destinations);
        });
        services.TryAddSingleton<IObjectStorageContext, S3ObjectStorage>();

        return new ObjectStorageBuilder(services, registry, Options.DefaultName);
    }

    /// <summary>
    /// Registers a named connection — one cloud account (endpoint, region, credentials). Storage contexts are
    /// bound to it with <see cref="AddObjectStorage{TContext}(IServiceCollection, string)"/>; several contexts
    /// sharing a name share one S3 client.
    /// </summary>
    public static ObjectStorageConnectionBuilder AddObjectStorageConnection(
        this IServiceCollection services, string name, Action<ObjectStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        var registry = AddConnectionCore(services, name, configure);

        return new ObjectStorageConnectionBuilder(services, registry, name);
    }

    /// <summary>
    /// Registers a storage context on its own connection, configured here. The connection is private to the
    /// context; to share one account between contexts use
    /// <see cref="AddObjectStorageConnection(IServiceCollection, string, Action{ObjectStorageOptions})"/>.
    /// </summary>
    public static ObjectStorageContextBuilder<TContext> AddObjectStorage<TContext>(
        this IServiceCollection services, Action<ObjectStorageOptions> configure)
        where TContext : ObjectStorageContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var connectionName = ConnectionNameOf(typeof(TContext));
        AddConnectionCore(services, connectionName, configure);

        return services.AddObjectStorage<TContext>(connectionName);
    }

    /// <summary>Registers a storage context on an existing named connection.</summary>
    public static ObjectStorageContextBuilder<TContext> AddObjectStorage<TContext>(
        this IServiceCollection services, string connectionName)
        where TContext : ObjectStorageContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);

        var registry = GetOrAddRegistry(services);
        AddCore(services);

        var contextType = typeof(TContext);
        var model = StorageModel.Build(contextType);      // property scan + destination validation at registration
        registry.AddContext(contextType, connectionName);

        services.AddSingleton(sp =>
        {
            if (!registry.HasConnection(connectionName))
                throw new InvalidOperationException(
                    $"Connection '{connectionName}' required by storage context {contextType.FullName} is not registered. " +
                    $"Call AddObjectStorageConnection(\"{connectionName}\", …) or AddObjectStorage<{contextType.Name}>(configure).");

            var options = sp.GetRequiredService<IOptionsMonitor<ObjectStorageOptions>>().Get(connectionName);
            var destinations = model.ResolveDestinations(
                options.Objects, options.BucketNamePrefix, options.BucketNameSuffix);

            var bucketSettings = BuildBucketSettings(
                model, destinations, options, registry.GetBucketSettings(contextType));

            var s3 = sp.GetRequiredService<IS3ClientFactory>().Get(connectionName);
            var context = ActivatorUtilities.CreateInstance<TContext>(sp);
            context.Initialize(new S3ObjectStorageClient(s3, destinations), model, bucketSettings);

            return context;
        });

        foreach (var property in model.Properties)
        {
            var metadataType = property.MetadataType;
            BucketRegistration.Register(services, registry, contextType, metadataType,
                sp => sp.GetRequiredService<TContext>().Bucket(metadataType),
                property.ServiceType);   // IObjectBucket<TMetadata> or IObjectBucket<TMetadata, TKey>
        }

        return new ObjectStorageContextBuilder<TContext>(services, registry, connectionName);
    }

    /// <summary>Connection name implied by a storage context type when it owns its connection.</summary>
    internal static string ConnectionNameOf(Type contextType) => contextType.FullName ?? contextType.Name;

    /// <summary>
    /// Bucket settings by physical bucket name: what was declared in code for the key, then what the connection
    /// configuration says for that bucket. Buckets shared by several keys accumulate all of it.
    /// </summary>
    internal static IReadOnlyDictionary<string, Action<BucketSettings>> BuildBucketSettings(
        StorageModel model,
        IReadOnlyDictionary<Type, string> destinations,
        ObjectStorageOptions options,
        IReadOnlyDictionary<string, Action<BucketSettings>> declaredInCode)
    {
        // Pass 1 — what the registration declared, re-keyed by physical bucket name (kernel shared with Testing).
        var result = new Dictionary<string, Action<BucketSettings>>(
            model.MapSettingsToBuckets(destinations, declaredInCode), StringComparer.OrdinalIgnoreCase);

        // Configuration is keyed by the bucket name as written in Objects (without the environment
        // prefix/suffix); the final name is accepted too, since usually they are the same. Every key is
        // present in Objects here — ResolveDestinations already required it.
        var configuredNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in model.Properties)
        {
            var bucketName = DestinationValidator.Split(destinations[property.MetadataType]).BucketName;
            configuredNames[bucketName] = DestinationValidator.Split(options.Objects[property.ConfigurationKey]).BucketName;
        }

        // Pass 2 — configuration on top of code, once per physical bucket even if several keys point to it.
        foreach (var (bucketName, configuredName) in configuredNames)
        {
            if (!options.Buckets.TryGetValue(configuredName, out var fromConfig)
                && !options.Buckets.TryGetValue(bucketName, out fromConfig))
                continue;

            result[bucketName] = result.TryGetValue(bucketName, out var existing)
                ? existing + fromConfig.Apply
                : fromConfig.Apply;
        }

        return result;
    }

    static ObjectStorageRegistry AddConnectionCore(IServiceCollection services, string name, Action<ObjectStorageOptions> configure)
    {
        var registry = GetOrAddRegistry(services);
        registry.AddConnection(name);

        services.AddOptions<ObjectStorageOptions>(name).Configure(configure).ValidateOnStart();
        AddCore(services);

        return registry;
    }

    static void AddCore(IServiceCollection services)
    {
        // TryAddEnumerable, so the validator is registered exactly once but still coexists with validators the
        // application registers on its own.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<ObjectStorageOptions>, ObjectStorageOptionsValidator>());
        services.TryAddSingleton<IS3ClientFactory, S3ClientFactory>();
    }

    // The registry is shared registration-time state, so it lives in the service collection as an instance and
    // is picked up by every subsequent AddObjectStorage* call (including the ObjectStorageBuilder compat ctor).
    internal static ObjectStorageRegistry GetOrAddRegistry(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (!descriptor.IsKeyedService
                && descriptor.ServiceType == typeof(ObjectStorageRegistry)
                && descriptor.ImplementationInstance is ObjectStorageRegistry existing)
                return existing;
        }

        var registry = new ObjectStorageRegistry();
        services.AddSingleton(registry);

        return registry;
    }
}
