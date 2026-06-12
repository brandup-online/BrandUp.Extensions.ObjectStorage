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
    /// Use path-style addressing (<c>{serviceUrl}/{bucket}</c>) instead of virtual-hosted-style
    /// (<c>{bucket}.{serviceUrl}</c>). Required for S3-compatible servers such as MinIO that do not
    /// support virtual-hosted addressing. Defaults to <see langword="false"/>.
    /// </summary>
    public bool ForcePathStyle { get; set; }
}

// credentialsProvider is injected via DI default-value binding: present only when a provider was
// registered through ObjectStorageBuilder.UseCredentialsProvider, otherwise null.
internal class ObjectStorageOptionsValidator(CredentialsProviderMarker? credentialsProvider = null)
    : IValidateOptions<ObjectStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, ObjectStorageOptions options)
    {
        if (string.IsNullOrEmpty(options.ServiceUrl))
            return ValidateOptionsResult.Fail($"Property {nameof(ObjectStorageOptions.ServiceUrl)} is required.");
        if (string.IsNullOrEmpty(options.AuthenticationRegion))
            return ValidateOptionsResult.Fail($"Property {nameof(ObjectStorageOptions.AuthenticationRegion)} is required.");

        // Static keys are required only when no credentials provider is registered.
        if (credentialsProvider is null)
        {
            var hasStaticKeys = !string.IsNullOrEmpty(options.AccessKeyId) && !string.IsNullOrEmpty(options.SecretAccessKey);
            if (!hasStaticKeys)
                return ValidateOptionsResult.Fail(
                    $"Either {nameof(ObjectStorageOptions.AccessKeyId)} and {nameof(ObjectStorageOptions.SecretAccessKey)} must be set, " +
                    "or a credentials provider must be registered via ObjectStorageBuilder.UseCredentialsProvider.");
        }

        return ValidateOptionsResult.Success;
    }
}
