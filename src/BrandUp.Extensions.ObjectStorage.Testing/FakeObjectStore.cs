namespace BrandUp.Extensions.ObjectStorage;

/// <summary>
/// In-memory хранилище для использования в тестах.
/// Инжектируется как синглтон — позволяет проверять состояние между операциями.
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
        lock (_sync)
        {
            if (_buckets.ContainsKey(bucketName))
                throw new InvalidOperationException($"Bucket '{bucketName}' already exists.");
            _buckets[bucketName] = new FakeBucketData(settings ?? new BucketSettings());
        }
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
