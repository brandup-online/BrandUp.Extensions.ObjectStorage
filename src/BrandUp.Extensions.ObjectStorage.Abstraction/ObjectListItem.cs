namespace BrandUp.Extensions.ObjectStorage;

/// <summary>
/// One entry of a bucket listing. Deliberately lightweight: S3 listings do not return user metadata (that
/// would cost a per-object request), and a typed identifier cannot be reconstructed from the key string —
/// so the raw object key is exposed instead.
/// </summary>
public sealed record ObjectListItem(string Key, long Size, string? ETag, DateTimeOffset? LastModified);
