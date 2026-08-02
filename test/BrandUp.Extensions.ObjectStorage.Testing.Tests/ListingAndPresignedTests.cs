using Microsoft.Extensions.DependencyInjection;

namespace BrandUp.Extensions.ObjectStorage;

/// <summary>Listing, upload options and presigned URLs through the fake.</summary>
public class ListingAndPresignedTests
{
    static (ServiceProvider Provider, FakeObjectStore Store) Build()
    {
        var services = new ServiceCollection();
        var builder = services.AddFakeObjectStorage<MediaStorage>()
            .WithBucketName("Photos", "media/photos")   // one shared bucket, two mapping prefixes
            .WithBucketName("Videos", "media/videos")
            .WithBucket("media");

        return (services.BuildServiceProvider(), builder.Store);
    }

    [Fact]
    public async Task ListAsync_TypedBucket_SeesOnlyItsOwnPrefix()
    {
        var (sp, _) = Build();
        using (sp)
        {
            var storage = sp.GetRequiredService<MediaStorage>();
            var photoId = Guid.NewGuid();

            await storage.Photos.UploadAsync(photoId, new PhotoMetadata(), new MemoryStream([1]));
            await storage.Videos.UploadAsync(Guid.NewGuid(), new VideoMetadata(), new MemoryStream([2, 3]));

            // Both live in bucket "media" under different mapping prefixes.
            var photos = await ToListAsync(storage.Photos.ListAsync());
            var videos = await ToListAsync(storage.Videos.ListAsync());

            var photo = Assert.Single(photos);
            Assert.Equal($"photos/{photoId:d}", photo.Key);
            Assert.Equal(1, photo.Size);
            Assert.NotNull(photo.ETag);
            Assert.NotNull(photo.LastModified);

            Assert.StartsWith("videos/", Assert.Single(videos).Key);

            // The raw (untyped) bucket lists everything, in lexicographic key order like S3.
            var all = await ToListAsync(storage.Client.GetBucket("media").ListAsync());
            Assert.Equal(2, all.Count);
            Assert.Equal(all.Select(i => i.Key).OrderBy(k => k, StringComparer.Ordinal), all.Select(i => i.Key));
        }
    }

    [Fact]
    public async Task ListAsync_MissingBucket_ThrowsNoSuchBucket()
    {
        var (sp, _) = Build();
        using (sp)
        {
            var bucket = sp.GetRequiredService<MediaStorage>().Client.GetBucket("missing");

            var ex = await Assert.ThrowsAsync<ObjectStorageException>(async () =>
            {
                await foreach (var _ in bucket.ListAsync()) { }
            });
            Assert.Equal("NoSuchBucket", ex.ErrorCode);
        }
    }

    [Fact]
    public async Task UploadOptions_AreStored()
    {
        var (sp, store) = Build();
        using (sp)
        {
            var storage = sp.GetRequiredService<MediaStorage>();
            var id = Guid.NewGuid();

            await storage.Photos.UploadAsync(id, new PhotoMetadata(), new MemoryStream([1]), new UploadOptions
            {
                ContentType = "image/jpeg",
                CacheControl = "public, max-age=3600"
            });

            var stored = store.GetObject("media", $"photos/{id:d}");
            Assert.NotNull(stored?.UploadOptions);
            Assert.Equal("image/jpeg", stored.UploadOptions.ContentType);
            Assert.Equal("public, max-age=3600", stored.UploadOptions.CacheControl);
        }
    }

    [Fact]
    public async Task UploadJson_SetsJsonContentType()
    {
        var (sp, store) = Build();
        using (sp)
        {
            var storage = sp.GetRequiredService<MediaStorage>();
            var id = Guid.NewGuid();

            await storage.Photos.UploadJsonAsync(id, new PhotoMetadata(), new { hello = "world" });

            var stored = store.GetObject("media", $"photos/{id:d}");
            Assert.Equal("application/json", stored?.UploadOptions?.ContentType);
        }
    }

    [Fact]
    public async Task PresignedUrls_CarryBucketKeyAndVerb()
    {
        var (sp, _) = Build();
        using (sp)
        {
            var storage = sp.GetRequiredService<MediaStorage>();
            var id = Guid.NewGuid();

            var read = await storage.Photos.GetPresignedReadUrlAsync(id, TimeSpan.FromMinutes(5));
            var write = await storage.Photos.GetPresignedWriteUrlAsync(id, TimeSpan.FromMinutes(5), "image/jpeg");

            Assert.Contains("/media/", read.AbsoluteUri);
            Assert.Contains($"photos/{id:d}", Uri.UnescapeDataString(read.AbsoluteUri));
            Assert.Contains("verb=GET", read.Query);
            Assert.Contains("expires=300", read.Query);

            Assert.Contains("verb=PUT", write.Query);
            Assert.Contains("content-type=image%2Fjpeg", write.Query);

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => storage.Photos.GetPresignedReadUrlAsync(id, TimeSpan.Zero));
        }
    }

    static async Task<List<ObjectListItem>> ToListAsync(IAsyncEnumerable<ObjectListItem> source)
    {
        var list = new List<ObjectListItem>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    public class PhotoMetadata : IObjectMetadata { }
    public class VideoMetadata : IObjectMetadata { }

    public class MediaStorage : ObjectStorageContext
    {
        [Bucket]
        public IObjectBucket<PhotoMetadata> Photos { get; private set; } = null!;

        [Bucket]
        public IObjectBucket<VideoMetadata> Videos { get; private set; } = null!;
    }
}
