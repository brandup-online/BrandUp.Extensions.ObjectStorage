using BrandUp.Extensions.ObjectStorage.Internals;

namespace BrandUp.Extensions.ObjectStorage;

/// <summary>
/// Settings writes are diff-based: only aspects the caller actually changed are sent. Critical for providers
/// that do not implement every aspect — MinIO rejects bucket ACL grants, so an untouched Access must not
/// produce a PutBucketAcl request (this is exactly what broke CI provisioning via EnsureBucketsAsync).
/// </summary>
public class BucketSettingsWriteTests
{
    [Fact]
    public async Task UpdateSettings_NoChanges_WritesNothing()
    {
        var s3 = new RecordingS3Client();
        var bucket = new S3ObjectBucket("test", s3);

        await bucket.UpdateSettingsAsync(_ => { });

        Assert.Empty(s3.Writes);
    }

    [Fact]
    public async Task UpdateSettings_OnlyChangedAspectIsWritten()
    {
        var s3 = new RecordingS3Client();
        var bucket = new S3ObjectBucket("test", s3);

        await bucket.UpdateSettingsAsync(s => s.Versioning = BucketVersioning.Enabled);

        Assert.Equal(["SetVersioning"], s3.Writes);
    }

    [Fact]
    public async Task UpdateSettings_SettingSameValue_WritesNothing()
    {
        var s3 = new RecordingS3Client { Access = BucketAccess.PublicRead };
        var bucket = new S3ObjectBucket("test", s3);

        await bucket.UpdateSettingsAsync(s => s.Access = BucketAccess.PublicRead);

        Assert.Empty(s3.Writes);
    }

    [Fact]
    public async Task UpdateSettings_LifecycleChange_WritesLifecycleOnly()
    {
        var s3 = new RecordingS3Client();
        var bucket = new S3ObjectBucket("test", s3);

        await bucket.UpdateSettingsAsync(s => s.LifecycleRules.Add(new("tmp", 7)));

        Assert.Equal(["SetLifecycle"], s3.Writes);
    }

    [Fact]
    public async Task CreateBucket_EmptySettings_OnlyCreates()
    {
        var s3 = new RecordingS3Client();
        var client = new S3ObjectStorageClient(s3, new Dictionary<Type, string>());

        await client.CreateBucketAsync("test", _ => { });

        Assert.Equal(["CreateBucket"], s3.Writes);
    }

    [Fact]
    public async Task CreateBucket_NonDefaultSettings_WritesChangedAspects()
    {
        var s3 = new RecordingS3Client();
        var client = new S3ObjectStorageClient(s3, new Dictionary<Type, string>());

        await client.CreateBucketAsync("test", s =>
        {
            s.Versioning = BucketVersioning.Enabled;
            s.Access = BucketAccess.PublicRead;
        });

        Assert.Equal("CreateBucket", s3.Writes[0]);
        Assert.Equal(["SetAccess", "SetVersioning"], s3.Writes.Skip(1).Order());
    }

    sealed class RecordingS3Client : IS3Client
    {
        public List<string> Writes { get; } = [];
        public BucketVersioning Versioning { get; set; } = BucketVersioning.Disabled;
        public BucketAccess Access { get; set; } = BucketAccess.Private;
        public List<LifecycleRule> Lifecycle { get; set; } = [];

        public Task<BucketVersioning> GetVersioningAsync(string bucketName, CancellationToken ct) => Task.FromResult(Versioning);
        public Task<BucketAccess> GetAccessAsync(string bucketName, CancellationToken ct) => Task.FromResult(Access);
        public Task<IReadOnlyList<LifecycleRule>> GetLifecycleAsync(string bucketName, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<LifecycleRule>>(Lifecycle);

        public Task SetVersioningAsync(string bucketName, BucketVersioning versioning, CancellationToken ct)
        {
            Writes.Add("SetVersioning");
            return Task.CompletedTask;
        }

        public Task SetAccessAsync(string bucketName, BucketAccess access, CancellationToken ct)
        {
            Writes.Add("SetAccess");
            return Task.CompletedTask;
        }

        public Task SetLifecycleAsync(string bucketName, IReadOnlyList<LifecycleRule> rules, CancellationToken ct)
        {
            Writes.Add("SetLifecycle");
            return Task.CompletedTask;
        }

        public Task CreateBucketAsync(string bucketName, CancellationToken ct)
        {
            Writes.Add("CreateBucket");
            return Task.CompletedTask;
        }

        public Task<bool> BucketExistsAsync(string bucketName, CancellationToken ct) => Task.FromResult(true);
        public Task DeleteBucketAsync(string bucketName, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<BucketInfo>> ListBucketsAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<S3StorageObject> UploadAsync(string bucketName, string objectKey, IDictionary<string, string> metadata, Stream stream, UploadOptions? options, CancellationToken ct)
            => throw new NotSupportedException();
        public IAsyncEnumerable<ObjectListItem> ListObjectsAsync(string bucketName, string? prefix, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<Uri> GetPresignedUrlAsync(string bucketName, string objectKey, TimeSpan expiresIn, bool forWrite, string? contentType, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<S3StorageObject?> FindAsync(string bucketName, string objectKey, IEnumerable<string> metadataKeys, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<Stream?> ReadAsync(string bucketName, string objectKey, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string bucketName, string objectKey, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CopyAsync(string sourceBucketName, string sourceObjectKey, string targetBucketName, string targetObjectKey,
            IDictionary<string, string> metadata, UploadOptions? options, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
