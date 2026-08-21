namespace BrandUp.Extensions.ObjectStorage.Internals;

/// <summary>
/// Refusal shared by the S3 and the fake bucket, so a copy across storage connections is rejected the same
/// way by both — and by neither with a quiet fallback through the process.
/// </summary>
internal static class CopyTarget
{
    public static InvalidOperationException Mismatch(IObjectBucket source, IObjectBucket target)
        => new($"Cannot copy from bucket '{source.Name}' to bucket '{target.Name}': a server-side copy requires " +
            "both buckets to belong to the same storage connection. Read the object and upload it to the target " +
            "bucket if the payload really has to travel through the application.");
}
