namespace BrandUp.Extensions.ObjectStorage.Internals;

/// <summary>
/// Registration-time state of the object storage model: which connections exist, which storage contexts are
/// bound to them and which metadata type is served by which context. A single instance lives in the
/// <see cref="Microsoft.Extensions.DependencyInjection.IServiceCollection"/> and is also resolvable at runtime
/// (the options validator uses it to decide whether static keys are required for a given connection).
/// </summary>
internal sealed class ObjectStorageRegistry
{
    /// <summary>Owner of the mappings registered through the connection-level <see cref="ObjectStorageBuilder"/>.</summary>
    public static readonly Type DefaultOwner = typeof(ObjectStorageBuilder);

    readonly HashSet<string> _connections = [];
    readonly HashSet<string> _credentialsProviders = [];
    readonly Dictionary<Type, string> _contexts = [];
    readonly Dictionary<Type, Type> _metadataOwners = [];
    readonly Dictionary<Type, Dictionary<string, Action<BucketSettings>>> _bucketSettings = [];

    public IReadOnlyCollection<string> Connections => _connections;
    public IReadOnlyDictionary<Type, string> Contexts => _contexts;

    public void AddConnection(string name) => _connections.Add(name);

    public bool HasConnection(string name) => _connections.Contains(name);

    public void MarkCredentialsProvider(string name) => _credentialsProviders.Add(name);

    public bool HasCredentialsProvider(string name) => _credentialsProviders.Contains(name);

    public void AddContext(Type contextType, string connectionName)
    {
        if (_contexts.TryGetValue(contextType, out var existing))
            throw new InvalidOperationException(
                $"Storage context {contextType.FullName} is already registered on connection '{Display(existing)}'.");

        _contexts[contextType] = connectionName;
    }

    /// <summary>
    /// Claims <paramref name="metadataType"/> for <paramref name="owner"/>. Returns <see langword="false"/> when
    /// another owner already claimed it — the metadata type is then reachable only through its context, because
    /// a bare <c>IObjectBucket&lt;TMetadata&gt;</c> registration would be ambiguous.
    /// </summary>
    public bool ClaimMetadata(Type metadataType, Type owner, out Type currentOwner)
    {
        if (_metadataOwners.TryGetValue(metadataType, out var existing))
        {
            currentOwner = existing;
            return existing == owner;
        }

        _metadataOwners[metadataType] = owner;
        currentOwner = owner;
        return true;
    }

    /// <summary>
    /// Settings declared in code for a bucket of a context, by configuration key. Several calls for one key
    /// compose in declaration order.
    /// </summary>
    public void ConfigureBucket(Type contextType, string key, Action<BucketSettings> configure)
    {
        if (!_bucketSettings.TryGetValue(contextType, out var byKey))
            _bucketSettings[contextType] = byKey = new(StringComparer.OrdinalIgnoreCase);

        byKey[key] = byKey.TryGetValue(key, out var existing) ? existing + configure : configure;
    }

    public IReadOnlyDictionary<string, Action<BucketSettings>> GetBucketSettings(Type contextType)
        => _bucketSettings.TryGetValue(contextType, out var byKey)
            ? byKey
            : new Dictionary<string, Action<BucketSettings>>();

    public static string Display(string connectionName)
        => connectionName.Length == 0 ? "(default)" : connectionName;

    public static string Display(Type owner)
        => owner == DefaultOwner ? "the default connection" : owner.FullName ?? owner.Name;
}
