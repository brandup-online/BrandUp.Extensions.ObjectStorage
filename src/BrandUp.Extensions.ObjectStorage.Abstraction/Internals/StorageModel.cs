using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace BrandUp.Extensions.ObjectStorage.Internals;

/// <summary>
/// One <see cref="IObjectBucket{TMetadata}"/> property of a storage context: which metadata type it serves,
/// where it points by default and how to fill it. The physical bucket name is resolved later, from the
/// configuration of the connection the context is bound to.
/// </summary>
internal sealed class StorageProperty
{
    static readonly MethodInfo GetBucketMethod = typeof(IObjectStorageClient)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Single(m => m.Name == nameof(IObjectStorageClient.GetBucket) && m.IsGenericMethodDefinition);

    readonly MethodInfo _getBucket;

    public StorageProperty(PropertyInfo property, Type metadataType, string configurationKey)
    {
        Property = property;
        MetadataType = metadataType;
        ConfigurationKey = configurationKey;
        _getBucket = GetBucketMethod.MakeGenericMethod(metadataType);
    }

    public PropertyInfo Property { get; }
    public Type MetadataType { get; }

    /// <summary>Key the bucket is looked up by — the attribute value or the property name.</summary>
    public string ConfigurationKey { get; }

    /// <summary>Asks the client for the bucket serving <see cref="MetadataType"/>.</summary>
    public object CreateBucket(IObjectStorageClient client)
    {
        try
        {
            return _getBucket.Invoke(client, null)!;
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            // Surface the client's own error instead of the reflection wrapper.
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

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
            var metadataType = GetBucketMetadataType(property.PropertyType);
            if (metadataType is null)
                continue;

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

            owners[metadataType] = property;
            properties.Add(new StorageProperty(property, metadataType, key));
        }

        return new StorageModel(contextType, properties);
    }

    /// <summary>Returns TMetadata when the property type is exactly <see cref="IObjectBucket{TMetadata}"/>.</summary>
    static Type? GetBucketMetadataType(Type propertyType)
        => propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(IObjectBucket<>)
            ? propertyType.GetGenericArguments()[0]
            : null;
}
