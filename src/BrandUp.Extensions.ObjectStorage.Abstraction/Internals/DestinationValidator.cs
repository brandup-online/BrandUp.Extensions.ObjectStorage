namespace BrandUp.Extensions.ObjectStorage.Internals;

/// <summary>
/// Single place where a bucket name and an object key prefix are normalized and validated, shared by the
/// runtime mapping API (which takes them combined as <c>bucket/prefix</c>) and by <see cref="BucketAttribute"/>.
/// </summary>
internal static class DestinationValidator
{
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
    /// Validates an object key prefix: letters, digits, '-', '.' and '/' as segment separator. Leading and
    /// trailing '/' are trimmed; an empty prefix becomes <see langword="null"/> (bucket root).
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
            if (!char.IsLetterOrDigit(c) && c != '/' && c != '-' && c != '.')
                throw new ArgumentException(
                    $"Invalid character '{c}' in prefix. Only letters, digits, '-', '.' and '/' are allowed.", paramName);
        }

        if (prefix.Contains(".."))
            throw new ArgumentException("Prefix contains consecutive '.' characters.", paramName);

        var trimmed = prefix.Trim('/');
        if (trimmed.Length == 0)
            return null;

        // '/' separates key segments; an empty segment would produce a key that is not addressable.
        if (trimmed.Contains("//") || prefix.StartsWith('/'))
            throw new ArgumentException("Prefix contains consecutive '/' characters.", paramName);

        return trimmed;
    }

    /// <summary>Composes the destination used by the mapping layer: <c>bucket</c> or <c>bucket/prefix</c>.</summary>
    public static string Combine(string bucketName, string? prefix)
        => prefix is null ? bucketName : $"{bucketName}/{prefix}";

    /// <summary>Normalizes a combined destination (<c>bucket</c> or <c>bucket/prefix</c>).</summary>
    public static string Normalize(string destination, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination, paramName);

        destination = destination.Trim().Trim('/');

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
