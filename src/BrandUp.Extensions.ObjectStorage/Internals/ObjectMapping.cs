using System.ComponentModel;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace BrandUp.Extensions.ObjectStorage.Internals;

internal class ObjectMapping
{
    const string ObjectKeyDelimiter = "_";
    static readonly string[] DateTimeFormats = ["yyyy-MM-dd", "o"];

    Type _objectType = null!;
    Func<IObjectMetadata> _factory = null!;
    Dictionary<string, PropertyAccessor> _properties = null!;

    private ObjectMapping() { }

    public string BucketName { get; private init; } = null!;
    public string? ObjectKeyPrefix { get; private init; }
    public IEnumerable<string> MetadataKeys => _properties.Keys;

    /// <param name="objectId">Already-serialized object identifier (see ObjectKeySerializer).</param>
    public string GetObjectKey(string objectId)
        => ObjectKeyPrefix is null ? objectId : string.Join(ObjectKeyDelimiter, ObjectKeyPrefix, objectId);

    public IDictionary<string, string> Serialize(IObjectMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var data = new Dictionary<string, string>();
        foreach (var (key, accessor) in _properties)
        {
            var value = accessor.Get(metadata);
            if (value is null)
                continue;

            var valueType = accessor.PropertyType;
            if (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(Nullable<>))
                valueType = Nullable.GetUnderlyingType(valueType)!;

            data[key] = valueType == typeof(string) ? (string)value : ConvertToString(value);
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

            if (value is null)
            {
                accessor.Set(obj, null);
                continue;
            }

            var valueType = accessor.PropertyType;
            if (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(Nullable<>))
                valueType = Nullable.GetUnderlyingType(valueType)!;

            accessor.Set(obj, ConvertFromString(value, valueType));
        }
        return obj;
    }

    public static ObjectMapping Create(Type objectType, string destination)
    {
        ArgumentNullException.ThrowIfNull(objectType);
        ArgumentException.ThrowIfNullOrEmpty(destination);

        destination = destination.ToLower().Trim();
        var (bucketName, objectKeyPrefix) = DestinationValidator.Split(destination);

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

    static string ConvertToString(object value)
    {
        if (value is DateTime date)
        {
            if (date.Kind == DateTimeKind.Local)
                date = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
            return date.TimeOfDay == TimeSpan.Zero
                ? date.ToString("yyyy-MM-dd")
                : date.ToString("o", CultureInfo.InvariantCulture);
        }
        var converter = TypeDescriptor.GetConverter(value.GetType());
        return converter.ConvertToInvariantString(value) ?? string.Empty;
    }

    static object ConvertFromString(string str, Type targetType)
    {
        if (targetType == typeof(string))
            return str;
        if (targetType == typeof(DateTime))
            return DateTime.ParseExact(str, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        var converter = TypeDescriptor.GetConverter(targetType);
        return converter.ConvertFromInvariantString(str)
            ?? throw new InvalidOperationException($"Cannot convert '{str}' to {targetType.Name}.");
    }

    readonly record struct PropertyAccessor(
        Type PropertyType,
        Func<object, object?> Get,
        Action<object, object?> Set);
}
