namespace BrandUp.Extensions.ObjectStorage;

/// <summary>
/// A point-in-time snapshot of S3 credentials. Carries an optional <see cref="SessionToken"/> for
/// temporary (STS) credentials and an optional <see cref="ExpiresUtc"/> after which they must be renewed.
/// </summary>
public sealed record ObjectStorageCredentials(
    string AccessKeyId,
    string SecretAccessKey,
    string? SessionToken,
    DateTimeOffset? ExpiresUtc);
