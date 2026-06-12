namespace BrandUp.Extensions.ObjectStorage;

/// <summary>
/// Supplies S3 credentials to the storage client. Intended for temporary (STS) credentials with a limited
/// lifetime: the implementation keeps the current credentials cached and renews them out-of-band (e.g. on a
/// timer before <see cref="ObjectStorageCredentials.ExpiresUtc"/>), so <see cref="GetCurrent"/> can be read
/// synchronously by the AWS SDK during request signing without blocking on a network call.
/// </summary>
public interface IObjectStorageCredentialsProvider
{
    /// <summary>Returns the currently valid credentials from the cache. Called synchronously by the SDK on refresh.</summary>
    ObjectStorageCredentials GetCurrent();

    /// <summary>Proactively refreshes the cached credentials if they have expired or are about to expire.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
