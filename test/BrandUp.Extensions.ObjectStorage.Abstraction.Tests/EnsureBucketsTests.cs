using System.Net;
using BrandUp.Extensions.ObjectStorage.Internals;

namespace BrandUp.Extensions.ObjectStorage;

/// <summary>
/// Provisioning behaviour of <see cref="ObjectStorageContext.EnsureBucketsAsync"/> against a client stub that
/// can simulate what a real provider does — including losing the create race.
/// </summary>
public class EnsureBucketsTests
{
    [Fact]
    public async Task EnsureBuckets_CreatesMissingOnly()
    {
        var client = new StubClient(existing: ["photos"]);
        var context = Create(client, Declared(("photos", null), ("videos", null)));

        await context.EnsureBucketsAsync();

        Assert.Equal(["videos"], client.Created);
    }

    [Fact]
    public async Task EnsureBuckets_WithoutDeclaredSettings_CreatesNothing()
    {
        var client = new StubClient(existing: []);
        var context = Create(client);

        await context.EnsureBucketsAsync();

        Assert.Empty(client.Created);
    }

    [Fact]
    public async Task EnsureBuckets_ManagesOnlyDeclaredBuckets()
    {
        var client = new StubClient(existing: []);
        var context = Create(client, Declared(("videos", null)));

        await context.EnsureBucketsAsync();

        Assert.Equal(["videos"], client.Created);
    }

    [Fact]
    public async Task EnsureBuckets_CallSiteDelegate_CoversAllBuckets()
    {
        var client = new StubClient(existing: []);
        var context = Create(client);

        await context.EnsureBucketsAsync(s => s.Versioning = BucketVersioning.Enabled);

        Assert.Equal(["photos", "videos"], client.Created);
        Assert.Equal(BucketVersioning.Enabled, client.Settings["photos"].Versioning);
    }

    [Fact]
    public async Task EnsureBuckets_AlreadyExistsRace_IsSwallowed()
    {
        // Bucket reports "does not exist", but creating it fails with 409 — another process just created it.
        var client = new StubClient(existing: []) { FailWith = Conflict() };
        var context = Create(client, Declared(("photos", null), ("videos", null)));

        await context.EnsureBucketsAsync();

        Assert.Equal(["photos", "videos"], client.Created);
    }

    [Fact]
    public async Task EnsureBuckets_OtherFailure_Propagates()
    {
        var client = new StubClient(existing: [])
        {
            FailWith = new ObjectStorageException("denied", HttpStatusCode.Forbidden, "AccessDenied", new Exception())
        };
        var context = Create(client, Declared(("photos", null)));

        var ex = await Assert.ThrowsAsync<ObjectStorageException>(() => context.EnsureBucketsAsync());
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }

    [Fact]
    public async Task EnsureBuckets_AppliesDeclaredSettingsOfCreatedBuckets()
    {
        var client = new StubClient(existing: ["photos"]);
        var context = Create(client, Declared(
            ("photos", s => s.Access = BucketAccess.PublicRead),
            ("videos", s => s.Versioning = BucketVersioning.Enabled)));

        await context.EnsureBucketsAsync();

        Assert.Equal(BucketVersioning.Enabled, client.Settings["videos"].Versioning);
        Assert.DoesNotContain("photos", client.Settings.Keys);   // existing bucket left untouched
    }

    [Fact]
    public async Task EnsureBuckets_ExistingBucket_IsNeverReconfigured()
    {
        var client = new StubClient(existing: ["photos", "videos"]);
        var context = Create(client, Declared(("photos", s => s.Access = BucketAccess.PublicRead)));

        await context.EnsureBucketsAsync(s => s.Versioning = BucketVersioning.Enabled);

        Assert.Empty(client.Created);
        Assert.Empty(client.Updated);
    }

    static Dictionary<string, Action<BucketSettings>> Declared(params (string Bucket, Action<BucketSettings>? Configure)[] entries)
        => entries.ToDictionary(e => e.Bucket, e => e.Configure ?? (_ => { }), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void IsBucketAlreadyExists_MatchesConflictAndKnownCodes()
    {
        Assert.True(Conflict().IsBucketAlreadyExists);
        Assert.True(new ObjectStorageException("x", HttpStatusCode.BadRequest, "BucketAlreadyExists", new Exception())
            .IsBucketAlreadyExists);
        Assert.False(new ObjectStorageException("x", HttpStatusCode.Forbidden, "AccessDenied", new Exception())
            .IsBucketAlreadyExists);
    }

    static ObjectStorageException Conflict()
        => new("bucket exists", HttpStatusCode.Conflict, "BucketAlreadyOwnedByYou", new Exception());

    static TestContext Create(StubClient client, IReadOnlyDictionary<string, Action<BucketSettings>>? declared = null)
    {
        var context = new TestContext();
        context.Initialize(client, StorageModel.Build(typeof(TestContext)), declared);
        return context;
    }

    public class PhotoMetadata : IObjectMetadata { }
    public class VideoMetadata : IObjectMetadata { }

    public class TestContext : ObjectStorageContext
    {
        [Bucket("photos")]
        public IObjectBucket<PhotoMetadata> Photos { get; private set; } = null!;

        [Bucket("videos")]
        public IObjectBucket<VideoMetadata> Videos { get; private set; } = null!;
    }

    sealed class StubClient(string[] existing) : IObjectStorageClient
    {
        readonly HashSet<string> _existing = new(existing, StringComparer.OrdinalIgnoreCase);

        public List<string> Created { get; } = [];
        public Dictionary<string, BucketSettings> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, BucketSettings> Updated { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ObjectStorageException? FailWith { get; set; }

        public IObjectBucket GetBucket(string bucketName) => new StubBucket(bucketName, this);

        public IObjectBucket<TMetadata> GetBucket<TMetadata>() where TMetadata : class, IObjectMetadata
            => new StubBucket<TMetadata>(typeof(TMetadata) == typeof(PhotoMetadata) ? "photos" : "videos", this);

        public Task CreateBucketAsync(string bucketName, Action<BucketSettings>? configure = null, CancellationToken cancellationToken = default)
        {
            Created.Add(bucketName);

            if (FailWith is not null)
                throw FailWith;

            var settings = new BucketSettings();
            configure?.Invoke(settings);
            Settings[bucketName] = settings;
            _existing.Add(bucketName);

            return Task.CompletedTask;
        }

        public bool Exists(string bucketName) => _existing.Contains(bucketName);

        public Task DropBucketAsync(string bucketName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<BucketInfo>> ListBucketsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    class StubBucket(string name, StubClient client) : IObjectBucket
    {
        public string Name { get; } = name;

        public Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(client.Exists(Name));

        public Task<BucketSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task UpdateSettingsAsync(Action<BucketSettings> configure, CancellationToken cancellationToken = default)
        {
            var settings = new BucketSettings();
            configure(settings);
            client.Updated[Name] = settings;

            return Task.CompletedTask;
        }
    }

    sealed class StubBucket<TMetadata>(string name, StubClient client) : StubBucket(name, client), IObjectBucket<TMetadata>
        where TMetadata : class, IObjectMetadata
    {
        public Task<ObjectItem<TMetadata>?> FindOneAsync(Guid objectId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Stream?> OpenReadAsync(Guid objectId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ObjectItem<TMetadata>> UploadAsync(Guid objectId, TMetadata metadata, Stream content, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteOneAsync(Guid objectId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
