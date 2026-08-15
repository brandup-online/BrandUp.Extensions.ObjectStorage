using BrandUp.Extensions.ObjectStorage.Internals;
using Microsoft.Extensions.Options;

namespace BrandUp.Extensions.ObjectStorage;

public class ObjectStorageOptions
{
    public string? ServiceUrl { get; set; }
    public string? AuthenticationRegion { get; set; }
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }

    /// <summary>
    /// Optional session token for temporary (STS) credentials. When set, the SDK signs requests with the
    /// <c>X-Amz-Security-Token</c> header. Ignored when an <see cref="IObjectStorageCredentialsProvider"/>
    /// is registered. Use for short-lived, non-refreshing scenarios; for auto-refresh use a provider.
    /// </summary>
    public string? SessionToken { get; set; }

    /// <summary>
    /// Where objects of this connection live: keyed by the property name of the storage context bucket or by the
    /// key declared in <see cref="BucketAttribute"/> (for the default connection — by the bucket name used in
    /// <c>AddMapping</c>). A value is <c>bucket</c> or <c>bucket/prefix</c>. Lookup is case-insensitive.
    /// </summary>
    public Dictionary<string, string> Objects { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Settings of the buckets themselves, used when a bucket is created (see
    /// <see cref="ObjectStorageContext.EnsureBucketsAsync"/>). Keyed by bucket name as written in
    /// <see cref="Objects"/> — without <see cref="BucketNamePrefix"/> and <see cref="BucketNameSuffix"/>.
    /// Only buckets listed here (or configured in code) are created automatically. Lookup is case-insensitive.
    /// </summary>
    public Dictionary<string, BucketSettingsOptions> Buckets { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Prefix prepended to bucket names, e.g. <c>dev-</c>. Lets one code base address per-environment buckets
    /// without listing every one of them.
    /// </summary>
    public string? BucketNamePrefix { get; set; }

    /// <summary>
    /// Suffix appended to bucket names, e.g. <c>-dev</c>. Combines with <see cref="BucketNamePrefix"/>.
    /// </summary>
    public string? BucketNameSuffix { get; set; }

    /// <summary>
    /// Use path-style addressing (<c>{serviceUrl}/{bucket}</c>) instead of virtual-hosted-style
    /// (<c>{bucket}.{serviceUrl}</c>). Required for S3-compatible servers such as MinIO that do not
    /// support virtual-hosted addressing. Defaults to <see langword="false"/>.
    /// </summary>
    public bool ForcePathStyle { get; set; }

    /// <summary>
    /// Size of a multipart upload part in bytes. S3 requires at least 5 MB per part (except the last);
    /// for very large seekable payloads the effective part size scales up automatically to fit the
    /// 10,000-part limit. Defaults to 16 MB.
    /// </summary>
    public int MultipartPartSize { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    /// Seekable payloads larger than this many bytes upload via the multipart API; smaller ones use a
    /// single PUT. Defaults to 64 MB.
    /// </summary>
    public long MultipartThreshold { get; set; } = 64L * 1024 * 1024;
}

// Both dependencies are injected via DI default-value binding. credentialsProvider is the legacy marker of the
// default connection; registry knows, per connection name, whether a provider was registered.
internal class ObjectStorageOptionsValidator(
    CredentialsProviderMarker? credentialsProvider = null,
    ObjectStorageRegistry? registry = null)
    : IValidateOptions<ObjectStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, ObjectStorageOptions options)
    {
        name ??= Options.DefaultName;
        var connection = name.Length == 0 ? string.Empty : $" of connection '{name}'";

        if (string.IsNullOrEmpty(options.ServiceUrl))
            return ValidateOptionsResult.Fail($"Property {nameof(ObjectStorageOptions.ServiceUrl)}{connection} is required.");
        if (string.IsNullOrEmpty(options.AuthenticationRegion))
            return ValidateOptionsResult.Fail($"Property {nameof(ObjectStorageOptions.AuthenticationRegion)}{connection} is required.");

        // S3 rejects multipart parts below 5 MB (except the last), so a smaller setting would fail at upload time.
        if (options.MultipartPartSize < 5 * 1024 * 1024)
            return ValidateOptionsResult.Fail(
                $"Property {nameof(ObjectStorageOptions.MultipartPartSize)}{connection} must be at least 5 MB.");

        // Payloads up to the threshold go through a single PUT, which S3 caps at 5 GB — a larger threshold
        // would transfer gigabytes only to be rejected server-side with EntityTooLarge.
        if (options.MultipartThreshold <= 0 || options.MultipartThreshold > S3Client.MaxSinglePutSize)
            return ValidateOptionsResult.Fail(
                $"Property {nameof(ObjectStorageOptions.MultipartThreshold)}{connection} must be between 1 byte and 5 GB (the S3 single-PUT limit).");

        var hasProvider = (registry?.HasCredentialsProvider(name) ?? false)
            || (name.Length == 0 && credentialsProvider is not null);

        // Static keys are required only when no credentials provider is registered for this connection.
        if (!hasProvider)
        {
            var hasStaticKeys = !string.IsNullOrEmpty(options.AccessKeyId) && !string.IsNullOrEmpty(options.SecretAccessKey);
            if (!hasStaticKeys)
                return ValidateOptionsResult.Fail(
                    $"Either {nameof(ObjectStorageOptions.AccessKeyId)} and {nameof(ObjectStorageOptions.SecretAccessKey)}{connection} must be set, " +
                    "or a credentials provider must be registered via UseCredentialsProvider.");
        }

        return ValidateOptionsResult.Success;
    }
}
