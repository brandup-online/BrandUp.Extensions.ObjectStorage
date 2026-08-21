namespace BrandUp.Extensions.ObjectStorage.Internals;

/// <summary>
/// Single place where a bucket name and an object key prefix are normalized and validated, shared by the
/// runtime mapping API (which takes them combined as <c>bucket/prefix</c>) and by <see cref="BucketAttribute"/>.
/// </summary>
internal static class DestinationValidator
{
    /// <summary>Delimiter used between a mapping prefix and the object identifier by default.</summary>
    public const char ObjectKeyPrefixDelimiter = '/';

    /// <summary>
    /// The one character a prefix may end with to become the delimiter itself: <c>photos_</c> composes keys as
    /// <c>photos_1f0f…</c> instead of <c>photos/1f0f…</c>, the layout 2.0.x wrote. It is the only candidate that
    /// is safe to reinterpret — '-' and '.' were already legal inside a prefix in released versions, so giving
    /// them a meaning would silently move the objects of an existing configuration.
    /// </summary>
    public const char ExplicitPrefixDelimiter = '_';

    /// <summary>Whether the prefix already ends with the delimiter to the object identifier.</summary>
    public static bool CarriesDelimiter(string prefix)
        => prefix.Length > 0 && (prefix[^1] == ExplicitPrefixDelimiter || prefix[^1] == ObjectKeyPrefixDelimiter);

    /// <summary>
    /// Composes the full object key from a mapping prefix and an already-serialized object identifier.
    /// The single place that knows the key layout, so the S3 and fake implementations cannot drift.
    /// </summary>
    public static string JoinKey(string? prefix, string objectId)
        => prefix is null ? objectId
            : CarriesDelimiter(prefix) ? prefix + objectId
            : $"{prefix}{ObjectKeyPrefixDelimiter}{objectId}";

    /// <summary>
    /// Composes the effective listing prefix from a mapping prefix and a caller-supplied key prefix
    /// (relative to the mapping prefix). A <see langword="null"/> key prefix lists the whole mapping scope.
    /// </summary>
    public static string? JoinListPrefix(string? prefix, string? keyPrefix)
        => prefix is null ? keyPrefix : JoinKey(prefix, keyPrefix ?? string.Empty);

    /// <summary>Validates a bucket name: letters, digits, '-' and '.', starting and ending with a letter or digit.</summary>
    public static string NormalizeBucketName(string bucketName, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName, paramName);

        bucketName = bucketName.Trim();

        foreach (var c in bucketName)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '.')
                throw new ArgumentException(
                    $"Invalid character '{c}' in bucket name. Only letters, digits, '-' and '.' are allowed.", paramName);
        }

        if (bucketName.Contains(".."))
            throw new ArgumentException("Bucket name contains consecutive '.' characters.", paramName);

        if (bucketName[0] == '-' || bucketName[0] == '.')
            throw new ArgumentException("Bucket name must start with a letter or digit.", paramName);

        if (bucketName[^1] == '-' || bucketName[^1] == '.')
            throw new ArgumentException("Bucket name must end with a letter or digit.", paramName);

        return bucketName;
    }

    /// <summary>
    /// Validates an object key prefix: letters, digits, '-', '.', '_' and '/' as segment separator. Leading and
    /// trailing '/' are trimmed; an empty prefix becomes <see langword="null"/> (bucket root). A prefix ending
    /// with '_' keeps that character as its delimiter to the object identifier; write it as <c>photos_/</c> to
    /// get the folder layout for a segment that itself ends with '_'.
    /// </summary>
    public static string? NormalizePrefix(string? prefix, string paramName)
    {
        if (prefix is null)
            return null;

        prefix = prefix.Trim();
        if (prefix.Length == 0)
            return null;

        foreach (var c in prefix)
        {
            if (!char.IsLetterOrDigit(c) && c != '/' && c != '-' && c != '.' && c != '_')
                throw new ArgumentException(
                    $"Invalid character '{c}' in prefix. Only letters, digits, '-', '.', '_' and '/' are allowed.", paramName);
        }

        if (prefix.Contains(".."))
            throw new ArgumentException("Prefix contains consecutive '.' characters.", paramName);

        var trimmed = prefix.Trim('/');
        if (trimmed.Length == 0)
            return null;

        // '/' separates key segments; an empty segment would produce a key that is not addressable.
        if (trimmed.Contains("//") || prefix.StartsWith('/'))
            throw new ArgumentException("Prefix contains consecutive '/' characters.", paramName);

        // A trailing '_' is the delimiter to the object identifier, so what precedes it must be a real prefix
        // segment: "photos__" or "media/_" would produce keys nobody meant to write.
        if (trimmed[^1] == ExplicitPrefixDelimiter && (trimmed.Length == 1 || !char.IsLetterOrDigit(trimmed[^2])))
            throw new ArgumentException(
                $"Prefix '{trimmed}' ends with the delimiter '{ExplicitPrefixDelimiter}', so the character before " +
                "it must be a letter or a digit.", paramName);

        // A trailing '/' is redundant ("photos/" is "photos") except right after the delimiter, where it is the
        // only way to ask for the folder "photos_/" instead of the delimiter layout "photos_<id>".
        if (prefix[^1] == ObjectKeyPrefixDelimiter && trimmed[^1] == ExplicitPrefixDelimiter)
            trimmed += ObjectKeyPrefixDelimiter;

        return trimmed;
    }

    /// <summary>Composes the destination used by the mapping layer: <c>bucket</c> or <c>bucket/prefix</c>.</summary>
    public static string Combine(string bucketName, string? prefix)
        => prefix is null ? bucketName : $"{bucketName}/{prefix}";

    /// <summary>Normalizes a combined destination (<c>bucket</c> or <c>bucket/prefix</c>).</summary>
    public static string Normalize(string destination, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination, paramName);

        // Only the leading '/' is dropped here: a trailing one belongs to the prefix, which is the part that
        // knows whether it is redundant ("photos/") or the escape of the delimiter layout ("photos_/").
        destination = destination.Trim().TrimStart('/');

        if (destination.Length == 0)
            throw new ArgumentException("Destination cannot be empty.", paramName);

        var (bucketName, prefix) = Split(destination);

        return Combine(NormalizeBucketName(bucketName, paramName), NormalizePrefix(prefix, paramName));
    }

    /// <summary>
    /// Splits a destination into the bucket name and the optional object key prefix — everything after
    /// the first <c>/</c>. The single place that knows the <c>bucket/prefix</c> syntax.
    /// </summary>
    public static (string BucketName, string? Prefix) Split(string destination)
    {
        var slash = destination.IndexOf('/');
        return slash == -1 ? (destination, null) : (destination[..slash], destination[(slash + 1)..]);
    }
}
