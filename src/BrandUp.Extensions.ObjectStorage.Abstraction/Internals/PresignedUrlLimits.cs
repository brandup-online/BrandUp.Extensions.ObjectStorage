namespace BrandUp.Extensions.ObjectStorage.Internals;

/// <summary>Shared presigned-URL constraints, so the fake rejects exactly what production rejects.</summary>
internal static class PresignedUrlLimits
{
    /// <summary>SigV4 caps presigned URLs at 7 days.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(7);

    /// <summary>The single expiry validation used by both the S3 and the fake implementations.</summary>
    public static void Validate(TimeSpan expiresIn, string paramName = "expiresIn")
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiresIn, TimeSpan.Zero, paramName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(expiresIn, MaxLifetime, paramName);
    }
}
