namespace BrandUp.Extensions.ObjectStorage.Integration;

/// <summary>
/// A <see cref="FactAttribute"/> that is skipped unless MinIO connection settings are present in the
/// environment. Locally (no <c>MINIO_SERVICE_URL</c>) the test is reported as skipped; in CI, where the
/// pipeline starts MinIO and sets the variables, it runs.
/// </summary>
public sealed class MinioFactAttribute : FactAttribute
{
    public MinioFactAttribute()
    {
        if (!MinioEnvironment.IsConfigured)
            Skip = MinioEnvironment.SkipReason;
    }
}

/// <summary>
/// A <see cref="TheoryAttribute"/> with the same MinIO environment gating as <see cref="MinioFactAttribute"/>.
/// </summary>
public sealed class MinioTheoryAttribute : TheoryAttribute
{
    public MinioTheoryAttribute()
    {
        if (!MinioEnvironment.IsConfigured)
            Skip = MinioEnvironment.SkipReason;
    }
}
