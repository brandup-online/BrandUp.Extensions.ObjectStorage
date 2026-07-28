using BrandUp.Extensions.ObjectStorage.Internals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BrandUp.Extensions.ObjectStorage;

/// <summary>
/// Configures the default (unnamed) connection: its credentials and its runtime type-to-destination mappings.
/// </summary>
public class ObjectStorageBuilder
{
    readonly IServiceCollection _services;
    readonly ObjectStorageRegistry _registry;
    readonly string _connectionName;

    public ObjectStorageBuilder(IServiceCollection services)
        : this(services, new ObjectStorageRegistry(), Options.DefaultName)
    {
        ArgumentNullException.ThrowIfNull(services);
    }

    internal ObjectStorageBuilder(IServiceCollection services, ObjectStorageRegistry registry, string connectionName)
    {
        _services = services;
        _registry = registry;
        _connectionName = connectionName;
    }

    /// <summary>Name of the connection being configured; empty for the default connection.</summary>
    public string ConnectionName => _connectionName;

    /// <summary>
    /// Registers a credentials provider supplying (typically temporary/STS) credentials. When registered,
    /// <see cref="ObjectStorageOptions.AccessKeyId"/>/<see cref="ObjectStorageOptions.SecretAccessKey"/> become
    /// optional, and the S3 client refreshes credentials in place without being recreated.
    /// </summary>
    public ObjectStorageBuilder UseCredentialsProvider<TProvider>()
        where TProvider : class, IObjectStorageCredentialsProvider
    {
        CredentialsRegistration.Add<TProvider>(_services, _registry, _connectionName);
        return this;
    }

    /// <inheritdoc cref="UseCredentialsProvider{TProvider}()"/>
    public ObjectStorageBuilder UseCredentialsProvider(Func<IServiceProvider, IObjectStorageCredentialsProvider> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        CredentialsRegistration.Add(_services, _registry, _connectionName, factory);
        return this;
    }

    /// <summary>
    /// Maps a metadata type to a destination (<c>bucket</c> or <c>bucket/prefix</c>) of this connection and
    /// registers <c>IObjectBucket&lt;TMetadata&gt;</c> for direct injection.
    /// </summary>
    public ObjectStorageBuilder AddMapping<TMetadata>(string destination)
        where TMetadata : class, IObjectMetadata
    {
        destination = DestinationValidator.Normalize(destination, nameof(destination));

        _services.Configure<ObjectStorageMappingsOptions>(opts =>
            opts.Destinations[typeof(TMetadata)] = destination);

        // Регистрация типизированного бакета для прямого внедрения через DI
        BucketRegistration.Register(_services, _registry, ObjectStorageRegistry.DefaultOwner, typeof(TMetadata),
            sp => sp.GetRequiredService<IObjectStorageClient>().GetBucket<TMetadata>());

        return this;
    }
}

/// <summary>Configures a named connection shared by one or more storage contexts.</summary>
public class ObjectStorageConnectionBuilder
{
    readonly IServiceCollection _services;
    readonly ObjectStorageRegistry _registry;
    readonly string _connectionName;

    internal ObjectStorageConnectionBuilder(IServiceCollection services, ObjectStorageRegistry registry, string connectionName)
    {
        _services = services;
        _registry = registry;
        _connectionName = connectionName;
    }

    /// <summary>Name of the connection being configured.</summary>
    public string ConnectionName => _connectionName;

    /// <inheritdoc cref="ObjectStorageBuilder.UseCredentialsProvider{TProvider}()"/>
    public ObjectStorageConnectionBuilder UseCredentialsProvider<TProvider>()
        where TProvider : class, IObjectStorageCredentialsProvider
    {
        CredentialsRegistration.Add<TProvider>(_services, _registry, _connectionName);
        return this;
    }

    /// <inheritdoc cref="ObjectStorageBuilder.UseCredentialsProvider{TProvider}()"/>
    public ObjectStorageConnectionBuilder UseCredentialsProvider(Func<IServiceProvider, IObjectStorageCredentialsProvider> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        CredentialsRegistration.Add(_services, _registry, _connectionName, factory);
        return this;
    }
}

/// <summary>
/// Configures a storage context and the connection it is bound to. Credentials configured here belong to the
/// connection, so they apply to every context sharing it.
/// </summary>
public class ObjectStorageContextBuilder<TContext>
    where TContext : ObjectStorageContext
{
    readonly ObjectStorageConnectionBuilder _connection;
    readonly ObjectStorageRegistry _registry;

    internal ObjectStorageContextBuilder(IServiceCollection services, ObjectStorageRegistry registry, string connectionName)
    {
        _connection = new ObjectStorageConnectionBuilder(services, registry, connectionName);
        _registry = registry;
    }

    /// <summary>Name of the connection this context is bound to.</summary>
    public string ConnectionName => _connection.ConnectionName;

    /// <summary>
    /// Settings for the bucket serving <typeparamref name="TMetadata"/>, applied when
    /// <see cref="ObjectStorageContext.EnsureBucketsAsync"/> creates it. Configuration
    /// (<see cref="ObjectStorageOptions.Buckets"/>) is applied on top of this.
    /// </summary>
    public ObjectStorageContextBuilder<TContext> ConfigureBucket<TMetadata>(Action<BucketSettings> configure)
        where TMetadata : class, IObjectMetadata
    {
        ArgumentNullException.ThrowIfNull(configure);

        var model = StorageModel.Build(typeof(TContext));
        var property = model.Properties.FirstOrDefault(p => p.MetadataType == typeof(TMetadata))
            ?? throw new InvalidOperationException(
                $"Storage context {typeof(TContext).Name} has no bucket for metadata type {typeof(TMetadata).FullName}.");

        _registry.ConfigureBucket(typeof(TContext), property.ConfigurationKey, configure);

        return this;
    }

    /// <inheritdoc cref="ConfigureBucket{TMetadata}(Action{BucketSettings})"/>
    /// <param name="key">Configuration key of the bucket — the value of <see cref="BucketAttribute"/> or the property name.</param>
    /// <param name="configure">Settings to apply.</param>
    public ObjectStorageContextBuilder<TContext> ConfigureBucket(string key, Action<BucketSettings> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(configure);

        var model = StorageModel.Build(typeof(TContext));
        if (!model.Properties.Any(p => string.Equals(p.ConfigurationKey, key, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"Storage context {typeof(TContext).Name} has no bucket with configuration key '{key}'. " +
                $"Known keys: {string.Join(", ", model.Properties.Select(p => p.ConfigurationKey))}.");

        _registry.ConfigureBucket(typeof(TContext), key, configure);

        return this;
    }

    /// <inheritdoc cref="ObjectStorageBuilder.UseCredentialsProvider{TProvider}()"/>
    public ObjectStorageContextBuilder<TContext> UseCredentialsProvider<TProvider>()
        where TProvider : class, IObjectStorageCredentialsProvider
    {
        _connection.UseCredentialsProvider<TProvider>();
        return this;
    }

    /// <inheritdoc cref="ObjectStorageBuilder.UseCredentialsProvider{TProvider}()"/>
    public ObjectStorageContextBuilder<TContext> UseCredentialsProvider(Func<IServiceProvider, IObjectStorageCredentialsProvider> factory)
    {
        _connection.UseCredentialsProvider(factory);
        return this;
    }
}
