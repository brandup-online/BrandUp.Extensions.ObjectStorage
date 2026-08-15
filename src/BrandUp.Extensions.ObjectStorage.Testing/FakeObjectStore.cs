using System.Net;
using BrandUp.Extensions.ObjectStorage.Internals;

namespace BrandUp.Extensions.ObjectStorage;

/// <summary>
/// In-memory store for use in tests. Injected as a singleton, so state can be inspected between operations.
/// </summary>
public class FakeObjectStore
{
    readonly object _sync = new();
    // Ordinal, like S3: bucket "Media" cannot exist (S3 names are lowercase-only), so a raw
    // GetBucket("Media") misses — same as production would.
    readonly Dictionary<string, FakeBucketData> _buckets = new(StringComparer.Ordinal);

    #region Public inspection API

    public bool BucketExists(string bucketName)
    {
        lock (_sync) return _buckets.ContainsKey(bucketName);
    }

    public IReadOnlyList<string> GetBucketNames()
    {
        lock (_sync) return [.. _buckets.Keys];
    }

    public int GetObjectCount(string bucketName)
    {
        lock (_sync)
            return _buckets.TryGetValue(bucketName, out var b) ? b.Objects.Count : 0;
    }

    public void Clear()
    {
        lock (_sync) _buckets.Clear();
    }

    #endregion

    #region Internal API used by fake implementations

    public void CreateBucket(string bucketName, BucketSettings? settings = null)
    {
        ValidateBucketName(bucketName);

        lock (_sync)
        {
            if (_buckets.ContainsKey(bucketName))
                throw new InvalidOperationException($"Bucket '{bucketName}' already exists.");
            _buckets[bucketName] = new FakeBucketData(settings ?? new BucketSettings());
        }
    }

    // Real S3 rejects invalid names at creation (400 InvalidBucketName). Accepting e.g. an uppercase name
    // here would create a bucket the mapping layer (which lowercases bucket names) could never reach.
    // Structural rules ('..', start/end with letter or digit) come from the shared DestinationValidator;
    // the ASCII-lowercase restriction is S3-specific and added on top.
    static void ValidateBucketName(string bucketName)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucketName);

        try
        {
            DestinationValidator.NormalizeBucketName(bucketName, nameof(bucketName));
        }
        catch (ArgumentException e)
        {
            throw Invalid(bucketName, e);
        }

        foreach (var c in bucketName)
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.')
                continue;

            throw Invalid(bucketName, new ArgumentException($"Invalid character '{c}' in bucket name.", nameof(bucketName)));
        }

        static ObjectStorageException Invalid(string bucketName, Exception inner) => new(
            $"Invalid bucket name '{bucketName}': S3 bucket names may contain only lowercase letters, digits, " +
            "'-' and '.', and must start and end with a letter or digit.",
            HttpStatusCode.BadRequest, "InvalidBucketName", inner);
    }

    public void DropBucket(string bucketName)
    {
        lock (_sync) _buckets.Remove(bucketName);
    }

    internal IReadOnlyList<BucketInfo> ListBuckets()
    {
        lock (_sync)
            return _buckets.Select(kv => new BucketInfo(kv.Key, kv.Value.CreatedAt)).ToList();
    }

    internal BucketSettings GetSettings(string bucketName)
    {
        lock (_sync)
            return GetBucket(bucketName).Settings.Clone();
    }

    internal void UpdateSettings(string bucketName, Action<BucketSettings> configure)
    {
        lock (_sync)
            configure(GetBucket(bucketName).Settings);
    }

    public void PutObject(string bucketName, string key, byte[] content, object metadata, UploadOptions? options = null)
    {
        // Same validation as CreateBucket — this public entry point auto-creates the bucket, and must not
        // become a backdoor for names creation would reject.
        ValidateBucketName(bucketName);

        lock (_sync)
            GetOrCreateBucket(bucketName).Objects[key] = new FakeStoredObject(content, metadata, options);
    }

    internal IReadOnlyList<ObjectListItem> ListObjects(string bucketName, string? prefix)
    {
        lock (_sync)
        {
            if (!_buckets.TryGetValue(bucketName, out var bucket))
                return [];

            // S3 lists keys in lexicographic order.
            return bucket.Objects
                .Where(kv => prefix is null || kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new ObjectListItem(kv.Key, kv.Value.Size, kv.Value.ETag, kv.Value.LastModified))
                .ToList();
        }
    }

    internal FakeStoredObject? GetObject(string bucketName, string key)
    {
        lock (_sync)
        {
            if (!_buckets.TryGetValue(bucketName, out var bucket))
                return null;
            return bucket.Objects.TryGetValue(key, out var obj) ? obj : null;
        }
    }

    internal bool DeleteObject(string bucketName, string key)
    {
        lock (_sync)
        {
            if (!_buckets.TryGetValue(bucketName, out var bucket))
                return false;
            return bucket.Objects.Remove(key);
        }
    }

    FakeBucketData GetBucket(string bucketName)
    {
        if (!_buckets.TryGetValue(bucketName, out var bucket))
            throw new InvalidOperationException($"Bucket '{bucketName}' does not exist.");
        return bucket;
    }

    FakeBucketData GetOrCreateBucket(string bucketName)
    {
        if (!_buckets.TryGetValue(bucketName, out var bucket))
        {
            bucket = new FakeBucketData(new BucketSettings());
            _buckets[bucketName] = bucket;
        }
        return bucket;
    }

    #endregion
}

internal sealed class FakeBucketData(BucketSettings settings)
{
    public BucketSettings Settings { get; } = settings;
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public Dictionary<string, FakeStoredObject> Objects { get; } = new(StringComparer.Ordinal);
}

internal sealed class FakeStoredObject(byte[] content, object metadata, UploadOptions? uploadOptions = null)
{
    public byte[] Content { get; } = content;
    public object Metadata { get; } = metadata;
    public UploadOptions? UploadOptions { get; } = uploadOptions;
    public DateTimeOffset LastModified { get; } = DateTimeOffset.UtcNow;
    public long Size => Content.Length;
    public string ETag { get; } = ComputeETag(content);

    static string ComputeETag(byte[] data)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        return $"\"{Convert.ToHexString(md5.ComputeHash(data)).ToLowerInvariant()}\"";
    }
}

internal static class BucketSettingsExtensions
{
    internal static BucketSettings Clone(this BucketSettings s) => new()
    {
        Versioning = s.Versioning,
        Access = s.Access,
        LifecycleRules = [.. s.LifecycleRules]
    };
}
