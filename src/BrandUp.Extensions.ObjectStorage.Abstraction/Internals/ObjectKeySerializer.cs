using System.Globalization;

namespace BrandUp.Extensions.ObjectStorage.Internals;

/// <summary>
/// Turns a typed object identifier into the object key string. Supported key types: <see cref="Guid"/>,
/// <see cref="string"/>, <see cref="int"/>, <see cref="long"/> and anything implementing <see cref="IObjectKey"/>.
/// Unsupported types fail fast — at model build for contexts, at bucket creation otherwise.
/// </summary>
internal static class ObjectKeySerializer
{
    public static bool IsSupported(Type keyType)
        => keyType == typeof(Guid)
            || keyType == typeof(string)
            || keyType == typeof(int)
            || keyType == typeof(long)
            || typeof(IObjectKey).IsAssignableFrom(keyType);

    public static Func<TKey, string> Get<TKey>()
        where TKey : notnull
    {
        // Guid keeps the exact historic format ("d"), so existing data stays addressable.
        if (typeof(TKey) == typeof(Guid))
            return static id => ((Guid)(object)id).ToString("d");

        if (typeof(TKey) == typeof(string))
            return static id => Validate((string)(object)id);

        if (typeof(TKey) == typeof(int))
            return static id => ((int)(object)id).ToString(CultureInfo.InvariantCulture);

        if (typeof(TKey) == typeof(long))
            return static id => ((long)(object)id).ToString(CultureInfo.InvariantCulture);

        if (typeof(IObjectKey).IsAssignableFrom(typeof(TKey)))
        {
            // ObjectKey validates the composed string itself; only hand-rolled IObjectKey
            // implementations need the extra scan.
            if (typeof(ObjectKey).IsAssignableFrom(typeof(TKey)))
                return static id => ((IObjectKey)id).ToKeyString();

            return static id => Validate(((IObjectKey)id).ToKeyString());
        }

        throw NotSupported(typeof(TKey));
    }

    public static InvalidOperationException NotSupported(Type keyType)
        => new($"Object key type {keyType.FullName} is not supported. " +
            $"Use Guid, string, int, long, or a type implementing {nameof(IObjectKey)} (e.g. derive from ObjectKey).");

    /// <summary>
    /// Characters S3 technically accepts but AWS documents as "characters to avoid": they routinely break
    /// consoles, XML listings and third-party tooling.
    /// </summary>
    const string AvoidCharacters = "\\{}^%`[]\"<>~#|";

    /// <summary>
    /// Object keys must be non-empty, printable and free of the AWS "characters to avoid";
    /// everything else (spaces, unicode, '&amp;', ':', …) is up to the caller.
    /// </summary>
    public static string Validate(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Object key cannot be empty.", nameof(key));

        // A leading '/' or an empty segment ("//") silently produces a different S3 "folder" than intended,
        // especially now that mapping prefixes join with '/'. A trailing '/' stays legal: folder markers.
        if (key[0] == '/')
            throw new ArgumentException($"Object key '{key}' must not start with '/'.", nameof(key));

        if (key.Contains("//"))
            throw new ArgumentException($"Object key '{key}' contains an empty segment (\"//\").", nameof(key));

        foreach (var c in key)
        {
            if (char.IsControl(c))
                throw new ArgumentException($"Object key '{key}' contains a control character.", nameof(key));

            if (AvoidCharacters.Contains(c))
                throw new ArgumentException(
                    $"Object key '{key}' contains character '{c}' from the AWS \"characters to avoid\" list ({AvoidCharacters}).",
                    nameof(key));
        }

        return key;
    }
}
