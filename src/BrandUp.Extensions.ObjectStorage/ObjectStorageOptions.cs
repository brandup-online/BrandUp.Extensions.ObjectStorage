using Microsoft.Extensions.Options;

namespace BrandUp.Extensions.ObjectStorage;

public class ObjectStorageOptions
{
    public string? ServiceUrl { get; set; }
    public string? AuthenticationRegion { get; set; }
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }
}

internal class ObjectStorageOptionsValidator : IValidateOptions<ObjectStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, ObjectStorageOptions options)
    {
        if (string.IsNullOrEmpty(options.ServiceUrl))
            return ValidateOptionsResult.Fail($"Property {nameof(ObjectStorageOptions.ServiceUrl)} is required.");
        if (string.IsNullOrEmpty(options.AuthenticationRegion))
            return ValidateOptionsResult.Fail($"Property {nameof(ObjectStorageOptions.AuthenticationRegion)} is required.");
        if (string.IsNullOrEmpty(options.AccessKeyId))
            return ValidateOptionsResult.Fail($"Property {nameof(ObjectStorageOptions.AccessKeyId)} is required.");
        if (string.IsNullOrEmpty(options.SecretAccessKey))
            return ValidateOptionsResult.Fail($"Property {nameof(ObjectStorageOptions.SecretAccessKey)} is required.");

        return ValidateOptionsResult.Success;
    }
}
