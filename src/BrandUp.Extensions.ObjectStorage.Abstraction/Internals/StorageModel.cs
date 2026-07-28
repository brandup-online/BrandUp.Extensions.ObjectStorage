using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace BrandUp.Extensions.ObjectStorage.Internals;

/// <summary>
/// One <see cref="IObjectBucket{TMetadata}"/> property of a storage context: which metadata type it serves,
/// where it points by default and how to fill it. The physical bucket name is resolved later, from the
/// configuration of the connection the context is bound to.
/// </summary>
internal sealed class StorageProperty
{
    static readonly MethodInfo GetBucketGuidMethod = typeof(IObjectStorageClient)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Single(m => m.Name == nameof(IObjectStorageClient.GetBucket)
            && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1);

    static readonly MethodInfo GetBucketTypedMethod = typeof(IObjectStorageClient)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Single(m => m.Name == nameof(IObjectStorageClient.GetBucket)
            && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 2);

    readonly Func<IObjectStorageClient, object> _getBucket;

    public StorageProperty(PropertyInfo property, Type metadataType, Type keyType, string configurationKey)
    {
        Property = property;
        MetadataType = metadataType;
        KeyType = keyType;
        ConfigurationKey = configurationKey;

        // Guid keys always use the richer historic shape (IObjectBucket<TMetadata> derives from
        // IObjectBucket<TMetadata, Guid>, so it satisfies both property shapes); other keys use the
        // two-argument overload. Compiled once, so client errors propagate without reflection wrappers.
        var method = keyType == typeof(Guid)
            ? GetBucketGuidMethod.MakeGenericMethod(metadataType)
            : GetBucketTypedMethod.MakeGenericMethod(metadataType, keyType);

        var client = Expression.Parameter(typeof(IObjectStorageClient), "client");
        _getBucket = Expression.Lambda<Func<IObjectStorageClient, object>>(
            Expression.Convert(Expression.Call(client, method), typeof(object)), client).Compile();
    }

    public PropertyInfo Property { get; }
    public Type MetadataType { get; }

    /// <summary>Type of the object identifier of this bucket; <see cref="Guid"/> for the historic shape.</summary>
    public Type KeyType { get; }

    /// <summary>Exact interface of the property — the DI service type of the bucket.</summary>
    public Type ServiceType => Property.PropertyType;

    /// <summary>Key the bucket is looked up by — the attribute value or the property name.</summary>
    public string ConfigurationKey { get; }

    /// <summary>Asks the client for the bucket serving <see cref="MetadataType"/>.</summary>
    public object CreateBucket(IObjectStorageClient client) => _getBucket(client);

    /// <summary>Assigns the bucket to the property; works with <c>init</c> and non-public setters.</summary>
    public void SetValue(object context, object bucket) => Property.SetValue(context, bucket);
}

/// <summary>
/// Bucket composition of a storage context type. Built once per type: property scan and validation of the
/// declared names happen at registration, not on first access.
/// </summary>
internal sealed class StorageModel
{
    static readonly ConcurrentDictionary<Type, StorageModel> Cache = new();

    StorageModel(Type contextType, IReadOnlyList<StorageProperty> properties)
    {
        ContextType = contextType;
        Properties = properties;
    }

    public Type ContextType { get; }
    public IReadOnlyList<StorageProperty> Properties { get; }

    /// <summary>
    /// Destinations of all buckets, keyed by metadata type. Bucket names come from <paramref name="buckets"/>
    /// and are then wrapped in <paramref name="namePrefix"/> / <paramref name="nameSuffix"/>; a bucket without a
    /// configured name is an error.
    /// </summary>
    /// <param name="buckets">Configured buckets of the connection.</param>
    /// <param name="namePrefix">Prefix prepended to bucket names.</param>
    /// <param name="nameSuffix">Suffix appended to bucket names.</param>
    /// <param name="fallbackToKey">
    /// Use the configuration key as the bucket name when it is not configured. Off for real connections, on for
    /// the in-memory test double, where a name is only a label.
    /// </param>
    public IReadOnlyDictionary<Type, string> ResolveDestinations(
        IReadOnlyDictionary<string, string>? buckets, string? namePrefix, string? nameSuffix, bool fallbackToKey = false)
    {
        var destinations = new Dictionary<Type, string>();

        foreach (var property in Properties)
        {
            var location = $"{ContextType.Name}.{property.Property.Name}";
            destinations[property.MetadataType] = BucketResolver.Resolve(
                property.ConfigurationKey, buckets, namePrefix, nameSuffix,
                fallbackDestination: fallbackToKey ? property.ConfigurationKey : null, location);
        }

        return destinations;
    }

    /// <summary>Property by configuration key (case-insensitive); throws listing the known keys.</summary>
    public StorageProperty RequireProperty(string configurationKey)
        => Properties.FirstOrDefault(p => string.Equals(p.ConfigurationKey, configurationKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Storage context {ContextType.Name} has no bucket with configuration key '{configurationKey}'. " +
                $"Known keys: {string.Join(", ", Properties.Select(p => p.ConfigurationKey))}.");

    /// <summary>Property by metadata type; throws when the context has no bucket for it.</summary>
    public StorageProperty RequireProperty(Type metadataType)
        => Properties.FirstOrDefault(p => p.MetadataType == metadataType)
            ?? throw new InvalidOperationException(
                $"Storage context {ContextType.Name} has no bucket for metadata type {metadataType.FullName}.");

    /// <summary>
    /// Re-keys per-configuration-key bucket settings by the physical bucket name of the resolved
    /// destinations, composing the delegates when several keys share one bucket. The kernel shared by the
    /// real registration and the Testing package.
    /// </summary>
    public IReadOnlyDictionary<string, Action<BucketSettings>> MapSettingsToBuckets(
        IReadOnlyDictionary<Type, string> destinations,
        IReadOnlyDictionary<string, Action<BucketSettings>> settingsByKey)
    {
        var result = new Dictionary<string, Action<BucketSettings>>(StringComparer.OrdinalIgnoreCase);
        if (settingsByKey.Count == 0)
            return result;

        foreach (var property in Properties)
        {
            if (!settingsByKey.TryGetValue(property.ConfigurationKey, out var configure))
                continue;

            var bucketName = DestinationValidator.Split(destinations[property.MetadataType]).BucketName;
            result[bucketName] = result.TryGetValue(bucketName, out var existing) ? existing + configure : configure;
        }

        return result;
    }

    public static StorageModel Build(Type contextType)
    {
        ArgumentNullException.ThrowIfNull(contextType);
        return Cache.GetOrAdd(contextType, Create);
    }

    static StorageModel Create(Type contextType)
    {
        if (!typeof(ObjectStorageContext).IsAssignableFrom(contextType))
            throw new InvalidOperationException($"Type {contextType.FullName} does not derive from {nameof(ObjectStorageContext)}.");

        if (contextType.IsAbstract)
            throw new InvalidOperationException($"Storage context {contextType.FullName} cannot be abstract.");

        var properties = new List<StorageProperty>();
        var owners = new Dictionary<Type, PropertyInfo>();

        foreach (var property in contextType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var shape = GetBucketShape(property.PropertyType);
            if (shape is null)
                continue;

            var (metadataType, keyType) = shape.Value;

            var attribute = property.GetCustomAttribute<BucketAttribute>()
                ?? throw new InvalidOperationException(
                    $"Property {contextType.Name}.{property.Name} is a bucket but has no [Bucket] attribute.");

            if (property.SetMethod is null)
                throw new InvalidOperationException(
                    $"Property {contextType.Name}.{property.Name} must have a setter (private set or init is enough).");

            if (owners.TryGetValue(metadataType, out var existing))
                throw new InvalidOperationException(
                    $"Metadata type {metadataType.FullName} is mapped twice in {contextType.Name}: " +
                    $"{existing.Name} and {property.Name}.");

            var location = $"{contextType.Name}.{property.Name}";
            var key = (attribute.Key ?? property.Name).Trim();

            if (key.Length == 0)
                throw new InvalidOperationException($"Configuration key of {location} cannot be empty.");

            if (!ObjectKeySerializer.IsSupported(keyType))
                throw new InvalidOperationException($"Bucket {location}: {ObjectKeySerializer.NotSupported(keyType).Message}");

            owners[metadataType] = property;
            properties.Add(new StorageProperty(property, metadataType, keyType, key));
        }

        return new StorageModel(contextType, properties);
    }

    /// <summary>
    /// Returns (TMetadata, TKey) when the property type is exactly <see cref="IObjectBucket{TMetadata}"/>
    /// (key = <see cref="Guid"/>) or <see cref="IObjectBucket{TMetadata, TKey}"/>.
    /// </summary>
    static (Type MetadataType, Type KeyType)? GetBucketShape(Type propertyType)
    {
        if (!propertyType.IsGenericType)
            return null;

        var definition = propertyType.GetGenericTypeDefinition();
        var arguments = propertyType.GetGenericArguments();

        if (definition == typeof(IObjectBucket<>))
            return (arguments[0], typeof(Guid));

        if (definition == typeof(IObjectBucket<,>))
            return (arguments[0], arguments[1]);

        return null;
    }
}
