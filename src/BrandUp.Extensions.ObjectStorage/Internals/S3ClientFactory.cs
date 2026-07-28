using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BrandUp.Extensions.ObjectStorage.Internals;

/// <summary>
/// Owns one S3 client per connection name. Storage contexts sharing a connection share the underlying
/// <c>AmazonS3Client</c> — its connection pool and its credentials refresh cycle.
/// </summary>
internal interface IS3ClientFactory
{
    IS3Client Get(string connectionName);
}

internal sealed class S3ClientFactory(IServiceProvider serviceProvider, IOptionsMonitor<ObjectStorageOptions> options)
    : IS3ClientFactory, IDisposable
{
    // Lazy, because GetOrAdd may run its factory more than once under contention and a discarded AmazonS3Client
    // would leak its connection pool.
    readonly ConcurrentDictionary<string, Lazy<S3Client>> _clients = new();

    public IS3Client Get(string connectionName)
    {
        ArgumentNullException.ThrowIfNull(connectionName);

        return _clients.GetOrAdd(connectionName, name => new Lazy<S3Client>(
            () => new S3Client(options.Get(name), ResolveCredentialsProvider(name)),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    // Providers are registered keyed by connection name; the unkeyed lookup keeps working for the default
    // connection, including providers registered by hand instead of through UseCredentialsProvider.
    IObjectStorageCredentialsProvider? ResolveCredentialsProvider(string connectionName)
        => serviceProvider.GetKeyedService<IObjectStorageCredentialsProvider>(connectionName)
            ?? (connectionName.Length == 0 ? serviceProvider.GetService<IObjectStorageCredentialsProvider>() : null);

    public void Dispose()
    {
        foreach (var client in _clients.Values)
        {
            if (client.IsValueCreated)
                client.Value.Dispose();
        }

        _clients.Clear();
    }
}
