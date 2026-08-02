namespace BrandUp.Extensions.ObjectStorage.Internals;

/// <summary>Shared presigned-URL constraints, so the fake rejects exactly what production rejects.</summary>
internal static class PresignedUrlLimits
{
    /// <summary>SigV4 caps presigned URLs at 7 days.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(7);
}
