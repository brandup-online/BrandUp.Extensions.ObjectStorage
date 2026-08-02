using BrandUp.Extensions.ObjectStorage.Internals;
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
        services.AddSingleton<IObjectStorageContext>(storage);

        return new FakeObjectStorageBuilder(services, store, client);
    }

    /// <summary>
    /// Registers a storage context backed by an in-memory store: the same context type as in production, with
    /// its buckets resolved from <see cref="BucketAttribute"/> declarations.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="store">Store to back the context with; a new one is created when omitted. Pass the same
    /// store to two contexts to emulate them sharing one account.</param>
    public static FakeObjectStorageBuilder<TContext> AddFakeObjectStorage<TContext>(
        this IServiceCollection services, FakeObjectStore? store = null)
        where TContext : ObjectStorageContext
    {
        ArgumentNullException.ThrowIfNull(services);

        store ??= new FakeObjectStore();

        var model = StorageModel.Build(typeof(TContext));
        var client = new FakeObjectStorageClient(store);
        var names = new FakeBucketNames();

        services.AddSingleton(sp =>
        {
            // Names are resolved when the context is created, so overrides can be applied by the builder first.
            // Unlike a real connection, a bucket without a configured name falls back to its key: in tests the
            // physical name is just a label, and WithBucketName is there when it matters.
            var destinations = model.ResolveDestinations(
                names.Objects, names.NamePrefix, names.NameSuffix, fallbackToKey: true);

            foreach (var (metadataType, destination) in destinations)
                client.AddMapping(metadataType, destination);

            names.Resolved = true;

            var context = ActivatorUtilities.CreateInstance<TContext>(sp);
            context.Initialize(client, model, model.MapSettingsToBuckets(destinations, names.Settings));

            return context;
        });

        var owners = GetOrAddOwners(services);

        foreach (var property in model.Properties)
        {
            var metadataType = property.MetadataType;
            var serviceType = property.ServiceType;   // IObjectBucket<TMetadata> or IObjectBucket<TMetadata, TKey>

            if (owners.Claim(metadataType, typeof(TContext), out var currentOwner))
            {
                services.AddSingleton(serviceType, sp => sp.GetRequiredService<TContext>().Bucket(metadataType));
                continue;
            }

            // Same rule (and same wording) as a real connection: an ambiguous bucket injection fails with
            // an explanation instead of silently binding to one of the contexts.
            var message = MetadataOwners.AmbiguityMessage(metadataType,
                DisplayOwner(currentOwner), DisplayOwner(typeof(TContext)));

            services.AddSingleton(serviceType, _ => throw new InvalidOperationException(message));
        }

        return new FakeObjectStorageBuilder<TContext>(services, store, client, names);
    }

    internal static string DisplayOwner(Type owner)
        => owner == typeof(FakeObjectStorageBuilder) ? "the default fake storage" : owner.FullName ?? owner.Name;

    // Registration-time state shared by every AddFakeObjectStorage* call in the collection.
    internal static MetadataOwners GetOrAddOwners(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (!descriptor.IsKeyedService
                && descriptor.ServiceType == typeof(MetadataOwners)
                && descriptor.ImplementationInstance is MetadataOwners existing)
                return existing;
        }

        var owners = new MetadataOwners();
        services.AddSingleton(owners);

        return owners;
    }

    // Bucket pre-creation shared by both fake builders. The name is taken verbatim (S3 bucket names
    // are lowercase-only, and the mapping layer already lowercases the names it derives).
    internal static void CreateBucket(FakeObjectStore store, string bucketName, Action<BucketSettings>? configure)
    {
        var settings = new BucketSettings();
        configure?.Invoke(settings);
        store.CreateBucket(bucketName, settings);
    }
}

/// <summary>Physical bucket names of a fake context, the test-time counterpart of the connection options.</summary>
public sealed class FakeBucketNames
{
    public Dictionary<string, string> Objects { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? NamePrefix { get; set; }
    public string? NameSuffix { get; set; }

    /// <summary>Bucket settings declared per configuration key, the counterpart of ConfigureBucket.</summary>
    internal Dictionary<string, Action<BucketSettings>> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set once the context has been created and its buckets resolved.</summary>
    internal bool Resolved { get; set; }
}

public class FakeObjectStorageBuilder(IServiceCollection services, FakeObjectStore store, FakeObjectStorageClient client)
{
    public FakeObjectStorageBuilder AddMapping<TMetadata>(string destination)
        where TMetadata : class, IObjectMetadata
    {
        client.AddMapping<TMetadata>(destination);

        // Same ownership rules as production: a metadata type mapped both here and in a fake context makes
        // the bare bucket injection ambiguous, and repeated AddMapping for one type stays last-wins.
        var owners = FakeObjectStorageServiceCollectionExtensions.GetOrAddOwners(services);
        if (owners.Claim(typeof(TMetadata), typeof(FakeObjectStorageBuilder), out var currentOwner))
        {
            services.AddSingleton<IObjectBucket<TMetadata>>(_ => client.GetBucket<TMetadata>());
        }
        else
        {
            var message = MetadataOwners.AmbiguityMessage(typeof(TMetadata),
                FakeObjectStorageServiceCollectionExtensions.DisplayOwner(currentOwner),
                FakeObjectStorageServiceCollectionExtensions.DisplayOwner(typeof(FakeObjectStorageBuilder)));

            services.AddSingleton<IObjectBucket<TMetadata>>(_ => throw new InvalidOperationException(message));
        }

        return this;
    }

    /// <summary>
    /// Предзаполняет бакет перед тестами.
    /// </summary>
    public FakeObjectStorageBuilder WithBucket(string bucketName, Action<BucketSettings>? configure = null)
    {
        FakeObjectStorageServiceCollectionExtensions.CreateBucket(store, bucketName, configure);
        return this;
    }
}

public class FakeObjectStorageBuilder<TContext>(
    IServiceCollection services,
    FakeObjectStore store,
    FakeObjectStorageClient client,
    FakeBucketNames names)
    where TContext : ObjectStorageContext
{
    /// <summary>In-memory store backing the context.</summary>
    public FakeObjectStore Store => store;

    /// <summary>Client the context is built on.</summary>
    public FakeObjectStorageClient Client => client;

    /// <summary>
    /// Overrides the physical name of a bucket, by context property name or by the logical name declared in
    /// <see cref="BucketAttribute"/>. The value may carry a key prefix too: <c>my-bucket/photos</c>.
    /// </summary>
    public FakeObjectStorageBuilder<TContext> WithBucketName(string key, string bucketName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);

        EnsureNotResolved();
        names.Objects[key] = bucketName;
        return this;
    }

    /// <summary>Prefix prepended to bucket names that were not overridden.</summary>
    public FakeObjectStorageBuilder<TContext> WithBucketNamePrefix(string prefix)
    {
        EnsureNotResolved();
        names.NamePrefix = prefix;
        return this;
    }

    /// <summary>Suffix appended to bucket names that were not overridden.</summary>
    public FakeObjectStorageBuilder<TContext> WithBucketNameSuffix(string suffix)
    {
        EnsureNotResolved();
        names.NameSuffix = suffix;
        return this;
    }

    /// <summary>
    /// Settings of a bucket, applied when <see cref="ObjectStorageContext.EnsureBucketsAsync"/> creates it —
    /// the test-time counterpart of ConfigureBucket at registration.
    /// </summary>
    /// <param name="key">Configuration key of the bucket: value of <see cref="BucketAttribute"/> or property name.</param>
    /// <param name="configure">Settings to apply.</param>
    public FakeObjectStorageBuilder<TContext> ConfigureBucket(string key, Action<BucketSettings> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(configure);

        // Same contract as the real registration: an unknown key is a registration-time error, not a silent no-op.
        var property = StorageModel.Build(typeof(TContext)).RequireProperty(key);

        EnsureNotResolved();
        names.Settings[property.ConfigurationKey] =
            names.Settings.TryGetValue(property.ConfigurationKey, out var existing)
                ? existing + configure
                : configure;

        return this;
    }

    // Names are baked into the context when it is first resolved; changing them afterwards would be a silent
    // no-op, so it is an error instead.
    void EnsureNotResolved()
    {
        if (names.Resolved)
            throw new InvalidOperationException(
                $"Buckets of {typeof(TContext).Name} are already resolved. Configure bucket names before the " +
                "context is resolved from the service provider.");
    }

    /// <summary>
    /// Предзаполняет бакет перед тестами.
    /// </summary>
    public FakeObjectStorageBuilder<TContext> WithBucket(string bucketName, Action<BucketSettings>? configure = null)
    {
        FakeObjectStorageServiceCollectionExtensions.CreateBucket(store, bucketName, configure);
        return this;
    }

    /// <summary>Registers the store itself so tests can inspect it; useful when several contexts share one store.</summary>
    public FakeObjectStorageBuilder<TContext> ExposeStore()
    {
        services.AddSingleton(store);
        return this;
    }
}
