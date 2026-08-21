using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace BrandUp.Extensions.ObjectStorage;

public class FakeStorageContextTests
{
    [Fact]
    public void AddFakeObjectStorage_Context_ResolvesWithPopulatedBuckets()
    {
        var services = new ServiceCollection();
        services.AddFakeObjectStorage<MediaStorage>();

        using var sp = services.BuildServiceProvider();
        var storage = sp.GetRequiredService<MediaStorage>();

        Assert.Equal("photos", storage.Photos.Name);
        Assert.Equal("videos", storage.Videos.Name);
        Assert.Same(storage.Photos, sp.GetRequiredService<IObjectBucket<PhotoMetadata>>());
    }

    [Fact]
    public async Task Context_RoundTripsThroughStore()
    {
        var services = new ServiceCollection();
        services.AddFakeObjectStorage<MediaStorage>().WithBucket("photos");

        using var sp = services.BuildServiceProvider();
        var storage = sp.GetRequiredService<MediaStorage>();

        var id = Guid.NewGuid();
        await storage.Photos.UploadAsync(id, new PhotoMetadata { FileName = "a.jpg" },
            new MemoryStream(Encoding.UTF8.GetBytes("content")));

        var found = await storage.Photos.FindOneAsync(id);
        Assert.NotNull(found);
        Assert.Equal("a.jpg", found.Metadata.FileName);

        // The context is an IObjectStorageContext over its own buckets.
        var viaStorage = await storage.FindAsync<PhotoMetadata>(id);
        Assert.NotNull(viaStorage);

        Assert.True(await storage.DeleteAsync<PhotoMetadata>(id));
        Assert.Null(await storage.Photos.FindOneAsync(id));
    }

    [Fact]
    public async Task Context_PrefixSeparatesObjectsInOneBucket()
    {
        var services = new ServiceCollection();
        var store = new FakeObjectStore();
        var builder = services.AddFakeObjectStorage<MediaStorage>(store).WithBucket("photos");
        services.AddFakeObjectStorage<MirrorStorage>(store).WithBucketName("photos", "photos/mirror");

        using var sp = services.BuildServiceProvider();
        var media = sp.GetRequiredService<MediaStorage>();
        var mirror = sp.GetRequiredService<MirrorStorage>();

        // Same bucket, same object id, different prefixes ("photos" root vs "photos/mirror").
        var id = Guid.NewGuid();
        await media.Photos.UploadAsync(id, new PhotoMetadata { FileName = "original.jpg" }, new MemoryStream([1]));
        await mirror.Photos.UploadAsync(id, new PhotoMetadata { FileName = "mirror.jpg" }, new MemoryStream([2]));

        Assert.Equal(2, builder.Store.GetObjectCount("photos"));
        Assert.Equal("original.jpg", (await media.Photos.FindOneAsync(id))!.Metadata.FileName);
        Assert.Equal("mirror.jpg", (await mirror.Photos.FindOneAsync(id))!.Metadata.FileName);

        // Deleting through one prefix leaves the other untouched.
        Assert.True(await mirror.Photos.DeleteOneAsync(id));
        Assert.NotNull(await media.Photos.FindOneAsync(id));
    }

    [Fact]
    public async Task WithBucketName_OverridesPhysicalName()
    {
        var services = new ServiceCollection();
        var builder = services.AddFakeObjectStorage<MediaStorage>()
            .WithBucketName("Photos", "it-photos")     // по имени свойства
            .WithBucketName("videos", "it-videos")     // по имени из атрибута
            .WithBucket("it-photos");

        using var sp = services.BuildServiceProvider();
        var storage = sp.GetRequiredService<MediaStorage>();

        Assert.Equal("it-photos", storage.Photos.Name);
        Assert.Equal("it-videos", storage.Videos.Name);

        await storage.Photos.UploadAsync(Guid.NewGuid(), new PhotoMetadata(), new MemoryStream([1]));
        Assert.Equal(1, builder.Store.GetObjectCount("it-photos"));
    }

    [Fact]
    public void WithBucketNamePrefixAndSuffix_AppliedToDeclaredNames()
    {
        var services = new ServiceCollection();
        services.AddFakeObjectStorage<MediaStorage>()
            .WithBucketNamePrefix("test-")
            .WithBucketNameSuffix("-1");

        using var sp = services.BuildServiceProvider();
        var storage = sp.GetRequiredService<MediaStorage>();

        Assert.Equal("test-photos-1", storage.Photos.Name);
        Assert.Equal("test-videos-1", storage.Videos.Name);
    }

    [Fact]
    public void DuplicateMetadataAcrossContexts_ContextsWork_BareBucketThrows()
    {
        var services = new ServiceCollection();
        services.AddFakeObjectStorage<MediaStorage>();
        services.AddFakeObjectStorage<MirrorStorage>().WithBucketName("photos", "mirror");

        using var sp = services.BuildServiceProvider();

        Assert.Equal("photos", sp.GetRequiredService<MediaStorage>().Photos.Name);
        Assert.Equal("mirror", sp.GetRequiredService<MirrorStorage>().Photos.Name);

        var ex = Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<IObjectBucket<PhotoMetadata>>());
        Assert.Contains(nameof(MirrorStorage), ex.Message);
        Assert.Contains(nameof(MediaStorage), ex.Message);
    }

    [Fact]
    public void WithBucketName_AfterContextResolved_Throws()
    {
        var services = new ServiceCollection();
        var builder = services.AddFakeObjectStorage<MediaStorage>();

        using var sp = services.BuildServiceProvider();
        _ = sp.GetRequiredService<MediaStorage>();

        var ex = Assert.Throws<InvalidOperationException>(() => builder.WithBucketName("Photos", "late"));
        Assert.Contains(nameof(MediaStorage), ex.Message);
        Assert.Throws<InvalidOperationException>(() => builder.WithBucketNamePrefix("late-"));
        Assert.Throws<InvalidOperationException>(() => builder.WithBucketNameSuffix("-late"));
    }

    [Fact]
    public async Task EnsureBucketsAsync_CreatesMissingBucketsOnce()
    {
        var services = new ServiceCollection();
        var builder = services.AddFakeObjectStorage<MediaStorage>();

        using var sp = services.BuildServiceProvider();
        var storage = sp.GetRequiredService<MediaStorage>();

        Assert.False(await storage.Photos.ExistsAsync());

        await storage.EnsureBucketsAsync(s => s.Versioning = BucketVersioning.Enabled);

        Assert.True(await storage.Photos.ExistsAsync());
        Assert.True(await storage.Videos.ExistsAsync());
        Assert.Equal(BucketVersioning.Enabled, (await storage.Photos.GetSettingsAsync()).Versioning);
        Assert.Equal(2, builder.Store.GetBucketNames().Count);

        // Idempotent: an existing bucket is left alone instead of being re-created.
        await storage.EnsureBucketsAsync();
        Assert.Equal(2, builder.Store.GetBucketNames().Count);
    }

    [Fact]
    public async Task EnsureBucketsAsync_OnlyDeclaredBuckets()
    {
        var services = new ServiceCollection();
        var builder = services.AddFakeObjectStorage<MediaStorage>()
            .ConfigureBucket("videos", s => s.Versioning = BucketVersioning.Enabled);

        using var sp = services.BuildServiceProvider();
        var storage = sp.GetRequiredService<MediaStorage>();

        await storage.EnsureBucketsAsync();

        Assert.Equal(["videos"], builder.Store.GetBucketNames());
        Assert.Equal(BucketVersioning.Enabled, (await storage.Videos.GetSettingsAsync()).Versioning);
        Assert.False(await storage.Photos.ExistsAsync());
    }

    [Fact]
    public async Task EnsureBucketsAsync_MixedCaseConfiguredName_SettingsStillApply()
    {
        // Bucket names are lowercased by the mapping layer while settings are keyed by the configured
        // (mixed-case) name — the lookup must survive the case difference.
        var services = new ServiceCollection();
        services.AddFakeObjectStorage<MediaStorage>()
            .WithBucketName("photos", "Prod-Photos")
            .ConfigureBucket("photos", s => s.Versioning = BucketVersioning.Enabled);

        using var sp = services.BuildServiceProvider();
        var storage = sp.GetRequiredService<MediaStorage>();

        await storage.EnsureBucketsAsync();

        Assert.Equal("prod-photos", storage.Photos.Name);
        Assert.True(await storage.Photos.ExistsAsync());
        Assert.Equal(BucketVersioning.Enabled, (await storage.Photos.GetSettingsAsync()).Versioning);
    }

    [Fact]
    public void ConfigureBucket_UnknownKey_Throws()
    {
        var services = new ServiceCollection();
        var builder = services.AddFakeObjectStorage<MediaStorage>();

        var ex = Assert.Throws<InvalidOperationException>(() => builder.ConfigureBucket("unknown", _ => { }));
        Assert.Contains("photos", ex.Message);
    }

    [Fact]
    public async Task EnsureBucketsAsync_TwoPropertiesOneBucket_CreatedOnce()
    {
        var services = new ServiceCollection();
        var builder = services.AddFakeObjectStorage<MediaStorage>()
            .WithBucketName("photos", "media/photos")
            .WithBucketName("videos", "media/videos")
            .ConfigureBucket("photos", _ => { });

        using var sp = services.BuildServiceProvider();
        var storage = sp.GetRequiredService<MediaStorage>();

        await storage.EnsureBucketsAsync();

        Assert.Equal(["media"], builder.Store.GetBucketNames());
    }

    [Fact]
    public async Task PrefixDelimiter_ComposesKeysWithoutFolder()
    {
        // A prefix written with a trailing '_' keeps the 2.0.x key layout: photos_<id>, not photos/<id>.
        var services = new ServiceCollection();
        var builder = services.AddFakeObjectStorage<MediaStorage>()
            .WithBucketName("photos", "media/photos_")
            .WithBucketName("videos", "media/videos")
            .WithBucket("media");

        using var sp = services.BuildServiceProvider();
        var storage = sp.GetRequiredService<MediaStorage>();
        var id = Guid.NewGuid();

        await storage.Photos.UploadAsync(id, new PhotoMetadata(), new MemoryStream([1]));

        Assert.NotNull(builder.Store.GetObject("media", $"photos_{id:d}"));
        Assert.NotNull(await storage.Photos.FindOneAsync(id));

        // Listing narrows to the same layout, so the bucket still sees only its own objects.
        var listed = await storage.Photos.ListAsync().ToListAsync();
        Assert.Equal($"photos_{id:d}", Assert.Single(listed).Key);

        await storage.Videos.UploadAsync(Guid.NewGuid(), new VideoMetadata(), new MemoryStream([2]));
        Assert.Single(await storage.Photos.ListAsync().ToListAsync());
    }

    [Fact]
    public async Task MissingBucket_BehavesLikeProduction()
    {
        var services = new ServiceCollection();
        services.AddFakeObjectStorage<MediaStorage>();   // buckets are not provisioned

        using var sp = services.BuildServiceProvider();
        var storage = sp.GetRequiredService<MediaStorage>();
        var id = Guid.NewGuid();

        // Upload fails with NoSuchBucket, like S3; reads stay lenient, like the real client.
        var ex = await Assert.ThrowsAsync<ObjectStorageException>(
            () => storage.Photos.UploadAsync(id, new PhotoMetadata(), new MemoryStream([1])));
        Assert.Equal("NoSuchBucket", ex.ErrorCode);

        Assert.Null(await storage.Photos.FindOneAsync(id));
        Assert.Null(await storage.Photos.OpenReadAsync(id));
        Assert.False(await storage.Photos.DeleteOneAsync(id));

        await Assert.ThrowsAsync<ObjectStorageException>(() => storage.Photos.GetSettingsAsync());
        await Assert.ThrowsAsync<ObjectStorageException>(() => storage.Client.DropBucketAsync("photos"));
    }

    [Fact]
    public void LegacyMapping_And_Context_SameMetadata_BareBucketThrows()
    {
        var services = new ServiceCollection();
        services.AddFakeObjectStorage().AddMapping<PhotoMetadata>("legacy-photos");
        services.AddFakeObjectStorage<MediaStorage>();

        using var sp = services.BuildServiceProvider();

        // The context still works; the bare bucket is ambiguous, same as mixing prod registrations.
        Assert.Equal("photos", sp.GetRequiredService<MediaStorage>().Photos.Name);
        var ex = Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<IObjectBucket<PhotoMetadata>>());
        Assert.Contains("the default fake storage", ex.Message);
    }

    [Fact]
    public async Task CrossTypeRead_MirrorsSchemaEvolution()
    {
        // Two mappings on one destination: written with the narrow type, read through the extended one —
        // production fills matching properties and leaves the rest default; the fake must do the same
        // instead of throwing InvalidCastException.
        var services = new ServiceCollection();
        services.AddFakeObjectStorage()
            .AddMapping<PhotoMetadata>("files")
            .AddMapping<ExtendedPhotoMetadata>("files")
            .WithBucket("files");

        using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IObjectStorageClient>();
        var id = Guid.NewGuid();

        await client.GetBucket<PhotoMetadata>().UploadAsync(id, new PhotoMetadata { FileName = "a.jpg" },
            new MemoryStream([1]));

        var extended = await client.GetBucket<ExtendedPhotoMetadata>().FindOneAsync(id);
        Assert.NotNull(extended);
        Assert.Equal("a.jpg", extended.Metadata.FileName);
        Assert.Equal(0, extended.Metadata.Version);
    }

    [Fact]
    public async Task RawBucketNames_AreCaseSensitive_LikeS3()
    {
        var services = new ServiceCollection();
        services.AddFakeObjectStorage<MediaStorage>().WithBucket("photos");

        using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<MediaStorage>().Client;

        // S3 bucket names are lowercase-only: an uppercase name never matches anything.
        Assert.True(await client.GetBucket("photos").ExistsAsync());
        Assert.False(await client.GetBucket("Photos").ExistsAsync());
        await Assert.ThrowsAsync<ObjectStorageException>(() => client.DropBucketAsync("PHOTOS"));
    }

    [Fact]
    public async Task PresignedUrl_LongerThanSevenDays_Throws()
    {
        var services = new ServiceCollection();
        services.AddFakeObjectStorage<MediaStorage>().WithBucket("photos");

        using var sp = services.BuildServiceProvider();
        var storage = sp.GetRequiredService<MediaStorage>();

        // SigV4 caps presigned URLs at 7 days; the fake enforces the same limit as production.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => storage.Photos.GetPresignedReadUrlAsync(Guid.NewGuid(), TimeSpan.FromDays(8)));
    }

    public class ExtendedPhotoMetadata : IObjectMetadata
    {
        public string? FileName { get; set; }
        public int Version { get; set; }
    }

    [Fact]
    public void SeparateContexts_HaveSeparateStores()
    {
        var services = new ServiceCollection();
        var media = services.AddFakeObjectStorage<MediaStorage>();
        var report = services.AddFakeObjectStorage<ReportStorage>();

        Assert.NotSame(media.Store, report.Store);
    }

    public class PhotoMetadata : IObjectMetadata
    {
        public string? FileName { get; set; }
    }

    public class VideoMetadata : IObjectMetadata { }

    public class ReportMetadata : IObjectMetadata { }

    public class MediaStorage : ObjectStorageContext
    {
        [Bucket("photos")]
        public IObjectBucket<PhotoMetadata> Photos { get; private set; } = null!;

        [Bucket("videos")]
        public IObjectBucket<VideoMetadata> Videos { get; private set; } = null!;
    }

    public class MirrorStorage : ObjectStorageContext
    {
        [Bucket("photos")]
        public IObjectBucket<PhotoMetadata> Photos { get; private set; } = null!;
    }

    public class ReportStorage : ObjectStorageContext
    {
        [Bucket("reports")]
        public IObjectBucket<ReportMetadata> Reports { get; private set; } = null!;
    }
}
