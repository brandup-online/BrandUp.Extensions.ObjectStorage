namespace BrandUp.Extensions.ObjectStorage;

/// <summary>
/// In-memory хранилище для использования в тестах.
/// Инжектируется как синглтон — позволяет проверять состояние между операциями.
/// </summary>
public class FakeObjectStore
{
    readonly object _sync = new();
    readonly Dictionary<string, FakeBucketData> _buckets = new(StringComparer.OrdinalIgnoreCase);

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

    public void PutObject(string bucketName, string key, byte[] content, object metadata)
    {
        lock (_sync)
            GetOrCreateBucket(bucketName).Objects[key] = new FakeStoredObject(content, metadata);
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

internal sealed class FakeStoredObject(byte[] content, object metadata)
{
    public byte[] Content { get; } = content;
    public object Metadata { get; } = metadata;
    public long Size => Content.Length;
    public string ETag { get; } = ComputeETag(content);

    static string ComputeETag(byte[] data)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        return $"\"{Convert.ToHexString(md5.ComputeHash(data)).ToLower()}\"";
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
