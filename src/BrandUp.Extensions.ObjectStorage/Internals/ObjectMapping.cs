using System.Linq.Expressions;
using System.Reflection;

namespace BrandUp.Extensions.ObjectStorage.Internals;

internal class ObjectMapping
{
    Type _objectType = null!;
    Func<IObjectMetadata> _factory = null!;
    Dictionary<string, PropertyAccessor> _properties = null!;

    private ObjectMapping() { }

    public string BucketName { get; private init; } = null!;
    public string? ObjectKeyPrefix { get; private init; }
    public IEnumerable<string> MetadataKeys => _properties.Keys;

    /// <param name="objectId">Already-serialized object identifier (see ObjectKeySerializer).</param>
    public string GetObjectKey(string objectId)
        => DestinationValidator.JoinKey(ObjectKeyPrefix, objectId);

    public IDictionary<string, string> Serialize(IObjectMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var data = new Dictionary<string, string>();
        foreach (var (key, accessor) in _properties)
        {
            var value = accessor.Get(metadata);
            if (value is null)
                continue;

            data[key] = MetadataValueConverter.ToInvariantString(value);
        }
        return data;
    }

    public IObjectMetadata Deserialize(IDictionary<string, string> data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var obj = _factory();
        foreach (var (key, value) in data)
        {
            if (!_properties.TryGetValue(key, out var accessor))
                throw new InvalidOperationException($"Type {_objectType.FullName} does not contain property '{key}'.");

            // Defense in depth: S3Client.FindAsync omits absent keys, so null should not occur — but a null
            // reaching the compiled setter would NRE on value-type properties, so skip it (leave the default).
            if (value is null)
                continue;

            var valueType = Nullable.GetUnderlyingType(accessor.PropertyType) ?? accessor.PropertyType;
            accessor.Set(obj, MetadataValueConverter.FromInvariantString(value, valueType));
        }
        return obj;
    }

    public static ObjectMapping Create(Type objectType, string destination)
    {
        ArgumentNullException.ThrowIfNull(objectType);
        ArgumentException.ThrowIfNullOrEmpty(destination);

        // Only the bucket name is lowered (S3 requires lowercase names; invariant — the Turkish locale
        // breaks culture-sensitive lowering). The key prefix keeps its case: S3 object keys are case-sensitive.
        var (bucketName, objectKeyPrefix) = DestinationValidator.Split(destination.Trim());
        bucketName = bucketName.ToLowerInvariant();

        var constructor = objectType.GetConstructor(BindingFlags.Instance | BindingFlags.Public, [])
            ?? throw new ArgumentException($"Type {objectType.FullName} has no public parameterless constructor.", nameof(objectType));

        var factory = Expression.Lambda<Func<IObjectMetadata>>(
            Expression.Convert(Expression.New(constructor), typeof(IObjectMetadata))).Compile();

        var properties = objectType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead && p.CanWrite)
            .ToDictionary(p => p.Name, p => CreateAccessor(objectType, p));

        return new ObjectMapping
        {
            _objectType = objectType,
            _factory = factory,
            _properties = properties,
            BucketName = bucketName,
            ObjectKeyPrefix = objectKeyPrefix
        };
    }

    static PropertyAccessor CreateAccessor(Type declaringType, PropertyInfo property)
    {
        var getter = PropertyGetters.Compile(property);

        var setObjParam = Expression.Parameter(typeof(object), "obj");
        var setValParam = Expression.Parameter(typeof(object), "val");
        var setter = Expression.Lambda<Action<object, object?>>(
            Expression.Assign(
                Expression.Property(Expression.Convert(setObjParam, declaringType), property),
                Expression.Convert(setValParam, property.PropertyType)),
            setObjParam, setValParam).Compile();

        return new PropertyAccessor(property.PropertyType, getter, setter);
    }

    readonly record struct PropertyAccessor(
        Type PropertyType,
        Func<object, object?> Get,
        Action<object, object?> Set);
}
