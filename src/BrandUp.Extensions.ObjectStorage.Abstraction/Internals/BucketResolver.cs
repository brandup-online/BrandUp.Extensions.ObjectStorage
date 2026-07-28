namespace BrandUp.Extensions.ObjectStorage.Internals;

/// <summary>
/// Turns a declared bucket (configuration key) into the destination actually used at run time. Bucket names and
/// object key prefixes live in the connection configuration, never in code.
/// </summary>
internal static class BucketResolver
{
    /// <summary>
    /// Looks the bucket up in <paramref name="buckets"/> by <paramref name="key"/>. The configured value is
    /// <c>bucket</c> or <c>bucket/prefix</c>; the bucket name is then wrapped in <paramref name="namePrefix"/>
    /// and <paramref name="nameSuffix"/>.
    /// </summary>
    /// <param name="key">Configuration key of the bucket.</param>
    /// <param name="buckets">Configured buckets of the connection.</param>
    /// <param name="namePrefix">Prefix prepended to the bucket name.</param>
    /// <param name="nameSuffix">Suffix appended to the bucket name.</param>
    /// <param name="fallbackDestination">
    /// Destination to use when the configuration has no entry for the key; <see langword="null"/> makes the
    /// entry required, which is the case for storage contexts.
    /// </param>
    /// <param name="location">Where the bucket is declared; used in error messages.</param>
    /// <param name="declaredPrefix">
    /// Object key prefix declared in code; kept when the configured value renames the bucket without carrying a
    /// prefix of its own. Storage contexts declare nothing, so they pass <see langword="null"/>.
    /// </param>
    public static string Resolve(
        string key,
        IReadOnlyDictionary<string, string>? buckets,
        string? namePrefix,
        string? nameSuffix,
        string? fallbackDestination,
        string location,
        string? declaredPrefix = null)
    {
        string destination;

        if (buckets is not null && buckets.TryGetValue(key, out var configured))
            destination = DestinationValidator.Normalize(configured, $"{location} ('{key}')");
        else
            destination = fallbackDestination ?? throw new InvalidOperationException(
                $"Bucket for {location} is not configured. Add it to the connection configuration " +
                $"as Objects:{key} (\"bucket\" or \"bucket/prefix\").");

        var slash = destination.IndexOf('/');
        var bucketName = slash == -1 ? destination : destination[..slash];
        var prefix = slash == -1 ? declaredPrefix : destination[(slash + 1)..];

        if (!string.IsNullOrEmpty(namePrefix) || !string.IsNullOrEmpty(nameSuffix))
            bucketName = namePrefix + bucketName + nameSuffix;

        return DestinationValidator.Combine(
            DestinationValidator.NormalizeBucketName(bucketName, location), prefix);
    }

    /// <summary>
    /// Resolution for a destination declared in code as <c>bucket</c> or <c>bucket/prefix</c>: the declared
    /// bucket name is both the configuration key and the fallback.
    /// </summary>
    public static string ResolveDestination(
        string destination,
        IReadOnlyDictionary<string, string>? buckets,
        string? namePrefix,
        string? nameSuffix,
        string location)
    {
        var slash = destination.IndexOf('/');
        var bucketName = slash == -1 ? destination : destination[..slash];
        var declaredPrefix = slash == -1 ? null : destination[(slash + 1)..];

        // Renaming the bucket in configuration must not silently drop the prefix declared in AddMapping.
        return Resolve(bucketName, buckets, namePrefix, nameSuffix, destination, location, declaredPrefix);
    }
}
