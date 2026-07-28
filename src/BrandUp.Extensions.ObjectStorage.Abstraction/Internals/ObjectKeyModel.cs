using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace BrandUp.Extensions.ObjectStorage.Internals;

/// <summary>
/// How an <see cref="ObjectKey"/>-derived type renders itself: parsed template (or the default
/// properties-joined-with-slash layout), compiled property getters and the normalized extension.
/// Built once per type; template errors surface on first use, not per call.
/// </summary>
internal sealed class ObjectKeyModel
{
    static readonly ConcurrentDictionary<Type, ObjectKeyModel> Cache = new();

    readonly Type _keyType;
    readonly IReadOnlyList<Segment> _segments;
    readonly string? _extension;

    ObjectKeyModel(Type keyType, IReadOnlyList<Segment> segments, string? extension)
    {
        _keyType = keyType;
        _segments = segments;
        _extension = extension;
    }

    public static ObjectKeyModel Get(Type keyType) => Cache.GetOrAdd(keyType, Create);

    public string Format(object key)
    {
        var sb = new StringBuilder();

        foreach (var segment in _segments)
        {
            if (segment.PropertyName is null)
            {
                sb.Append(segment.Literal);
                continue;
            }

            var value = segment.Getter!(key)
                ?? throw new InvalidOperationException(
                    $"Property {_keyType.Name}.{segment.PropertyName} of the object key is null; " +
                    "every property taking part in the key must be set.");

            var formatted = FormatValue(value, segment.ValueFormat);
            if (formatted.Length == 0)
                throw new InvalidOperationException(
                    $"Property {_keyType.Name}.{segment.PropertyName} of the object key produced an empty " +
                    "segment; every property taking part in the key must have a non-empty value.");

            sb.Append(formatted);
        }

        if (_extension is not null)
            sb.Append(_extension);

        return ObjectKeySerializer.Validate(sb.ToString());
    }

    static ObjectKeyModel Create(Type keyType)
    {
        var attribute = keyType.GetCustomAttribute<ObjectKeyFormatAttribute>();

        // Reflection does not guarantee property order, and for the default layout the order IS the key.
        // Make it deterministic: base type first, declaration order within a type.
        var properties = keyType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .OrderBy(p => InheritanceDepth(p.DeclaringType!))
            .ThenBy(p => p.MetadataToken)
            .ToArray();

        var segments = attribute?.Format is not null
            ? ParseTemplate(keyType, attribute.Format, properties)
            : DefaultSegments(keyType, properties);

        if (!segments.Any(s => s.PropertyName is not null))
            throw new InvalidOperationException(
                $"Object key {keyType.FullName} has no properties taking part in the key — " +
                "the key string would be the same for every object.");

        return new ObjectKeyModel(keyType, segments, NormalizeExtension(keyType, attribute?.Extension));
    }

    static int InheritanceDepth(Type type)
    {
        var depth = 0;
        for (var current = type; current is not null; current = current.BaseType)
            depth++;
        return depth;
    }

    /// <summary>Default layout: all public readable properties, joined with '/'.</summary>
    static List<Segment> DefaultSegments(Type keyType, PropertyInfo[] properties)
    {
        if (properties.Length == 0)
            throw new InvalidOperationException($"Object key {keyType.FullName} has no public properties.");

        var segments = new List<Segment>();
        foreach (var property in properties)
        {
            if (segments.Count > 0)
                segments.Add(Segment.OfLiteral("/"));
            segments.Add(Segment.OfProperty(property, null));
        }

        return segments;
    }

    static List<Segment> ParseTemplate(Type keyType, string template, PropertyInfo[] properties)
    {
        var segments = new List<Segment>();
        var literal = new StringBuilder();

        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];

            // Braces are placeholder syntax only: they are on the AWS "characters to avoid" list,
            // so a literal brace could never survive key validation anyway.
            if (c == '}')
                throw TemplateError(keyType, template, "unmatched '}'");

            if (c != '{')
            {
                literal.Append(c);
                continue;
            }

            var end = template.IndexOf('}', i + 1);
            if (end < 0)
                throw TemplateError(keyType, template, "unmatched '{'");

            if (literal.Length > 0)
            {
                segments.Add(Segment.OfLiteral(literal.ToString()));
                literal.Clear();
            }

            var placeholder = template[(i + 1)..end];
            var colon = placeholder.IndexOf(':');
            var name = colon < 0 ? placeholder : placeholder[..colon];
            var valueFormat = colon < 0 ? null : placeholder[(colon + 1)..];

            var property = properties.FirstOrDefault(p => p.Name == name)
                ?? throw TemplateError(keyType, template, $"unknown property '{name}'");

            segments.Add(Segment.OfProperty(property, string.IsNullOrEmpty(valueFormat) ? null : valueFormat));
            i = end;
        }

        if (literal.Length > 0)
            segments.Add(Segment.OfLiteral(literal.ToString()));

        return segments;
    }

    static string FormatValue(object value, string? format) => value switch
    {
        string s => s,
        Guid g => g.ToString(format ?? "d", CultureInfo.InvariantCulture),
        IObjectKey key => key.ToKeyString(),
        IFormattable f => f.ToString(format, CultureInfo.InvariantCulture),
        _ => TypeDescriptor.GetConverter(value.GetType()).ConvertToInvariantString(value)
            ?? throw new InvalidOperationException($"Cannot convert {value.GetType().Name} to an object key segment.")
    };

    static string? NormalizeExtension(Type keyType, string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return null;

        extension = extension.Trim();
        if (extension[0] != '.')
            extension = "." + extension;

        foreach (var c in extension[1..])
        {
            if (!char.IsLetterOrDigit(c) && c != '.')
                throw new InvalidOperationException(
                    $"Extension '{extension}' of object key {keyType.FullName} contains an invalid character '{c}'.");
        }

        if (extension.Length == 1)
            throw new InvalidOperationException($"Extension of object key {keyType.FullName} cannot be empty.");

        return extension;
    }

    static InvalidOperationException TemplateError(Type keyType, string template, string reason)
        => new($"Invalid key template '{template}' of object key {keyType.FullName}: {reason}.");

    sealed record Segment(string? Literal, string? PropertyName, Func<object, object?>? Getter, string? ValueFormat)
    {
        public static Segment OfLiteral(string literal) => new(literal, null, null, null);

        public static Segment OfProperty(PropertyInfo property, string? valueFormat)
            => new(null, property.Name, PropertyGetters.Compile(property), valueFormat);
    }
}
