namespace BrandUp.Extensions.ObjectStorage.Integration;

/// <summary>
/// Reads MinIO/S3 connection settings from environment variables. When <see cref="ServiceUrl"/> is not set
/// the integration tests are skipped, so the suite stays green locally without a running MinIO.
/// </summary>
static class MinioEnvironment
{
    public const string SkipReason = "MINIO_SERVICE_URL is not configured; skipping MinIO integration test.";

    public static string? ServiceUrl => Environment.GetEnvironmentVariable("MINIO_SERVICE_URL");
    public static string? AccessKey => Environment.GetEnvironmentVariable("MINIO_ACCESS_KEY");
    public static string? SecretKey => Environment.GetEnvironmentVariable("MINIO_SECRET_KEY");
    public static string Region => Environment.GetEnvironmentVariable("MINIO_REGION") ?? "us-east-1";

    public static bool IsConfigured => !string.IsNullOrEmpty(ServiceUrl);

    /// <summary>The single place the MinIO connection settings are applied, shared by fixture and tests.</summary>
    public static void Apply(ObjectStorageOptions options)
    {
        options.ServiceUrl = ServiceUrl;
        options.AuthenticationRegion = Region;
        options.AccessKeyId = AccessKey;
        options.SecretAccessKey = SecretKey;
        options.ForcePathStyle = true; // MinIO does not support virtual-hosted-style addressing
    }
}
