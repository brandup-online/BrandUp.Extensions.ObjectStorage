using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace BrandUp.Extensions.ObjectStorage.Integration;

/// <summary>
/// Integration tests that exercise the real S3 code path against a MinIO server. Gated by
/// <see cref="MinioFactAttribute"/>: skipped unless <c>MINIO_SERVICE_URL</c> is set, so they run in CI
/// (where the pipeline starts MinIO) but stay skipped locally.
/// </summary>
[Trait("Category", "Integration")]
public class MinioStorageTests(MinioFixture fixture) : IClassFixture<MinioFixture>
{
    [MinioFact]
    public async Task Bucket_Lifecycle_CreateExistsListDrop()
    {
        var name = "it" + Guid.NewGuid().ToString("n");

        await fixture.Client.CreateBucketAsync(name);
        try
        {
            var bucket = fixture.Client.GetBucket(name);
            Assert.True(await bucket.ExistsAsync());

            var buckets = await fixture.Client.ListBucketsAsync();
            Assert.Contains(buckets, b => b.Name == name);
        }
        finally
        {
            await fixture.Client.DropBucketAsync(name);
        }

        Assert.False(await fixture.Client.GetBucket(name).ExistsAsync());
    }

    [MinioFact]
    public async Task Object_Upload_Find_Read_Delete_RoundTrip()
    {
        var bucket = fixture.Client.GetBucket<TestFileMetadata>();
        var id = Guid.NewGuid();
        var metadata = new TestFileMetadata { FileName = "пример.txt", ContentType = "text/plain" };
        var payload = Encoding.UTF8.GetBytes("hello minio");

        var uploaded = await bucket.UploadAsync(id, metadata, new MemoryStream(payload));
        try
        {
            Assert.Equal(id, uploaded.Id);
            Assert.Equal(payload.Length, uploaded.Size);

            var found = await bucket.FindOneAsync(id);
            Assert.NotNull(found);
            Assert.Equal("пример.txt", found!.Metadata.FileName);
            Assert.Equal("text/plain", found.Metadata.ContentType);

            await using var stream = await bucket.OpenReadAsync(id);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!);
            Assert.Equal("hello minio", await reader.ReadToEndAsync());
        }
        finally
        {
            Assert.True(await bucket.DeleteOneAsync(id));
        }

        Assert.Null(await bucket.FindOneAsync(id));
    }

    [MinioFact]
    public async Task Object_Json_RoundTrip()
    {
        var bucket = fixture.Client.GetBucket<TestFileMetadata>();
        var id = Guid.NewGuid();
        var metadata = new TestFileMetadata { FileName = "data.json", ContentType = "application/json" };
        var content = new Payload(42, "answer");

        await bucket.UploadJsonAsync(id, metadata, content);
        try
        {
            var restored = await bucket.ReadJsonAsync<TestFileMetadata, Payload>(id);
            Assert.NotNull(restored);
            Assert.Equal(42, restored!.Number);
            Assert.Equal("answer", restored.Text);
        }
        finally
        {
            await bucket.DeleteOneAsync(id);
        }
    }

    [MinioFact]
    public async Task Object_SchemaEvolution_OldObjectReadableThroughExtendedMetadata()
    {
        // Written with the old schema...
        var bucket = fixture.Client.GetBucket<TestFileMetadata>();
        var id = Guid.NewGuid();
        await bucket.UploadAsync(id, new TestFileMetadata { FileName = "old.txt", ContentType = "text/plain" },
            new MemoryStream([1, 2, 3]));

        try
        {
            // ...and read through a metadata class that gained new properties, including value types:
            // absent keys must leave the properties at their defaults instead of failing the read.
            var extended = await fixture.Client.GetBucket<ExtendedTestFileMetadata>().FindOneAsync(id);

            Assert.NotNull(extended);
            Assert.Equal("old.txt", extended.Metadata.FileName);
            Assert.Equal(0, extended.Metadata.Version);
            Assert.Equal(default, extended.Metadata.ArchivedAt);
        }
        finally
        {
            await bucket.DeleteOneAsync(id);
        }
    }

    [MinioFact]
    public async Task Object_Empty_RoundTrip()
    {
        // Empty objects are legal in S3: markers, placeholders.
        var bucket = fixture.Client.GetBucket<TestFileMetadata>();
        var id = Guid.NewGuid();

        var item = await bucket.UploadAsync(id, new TestFileMetadata { FileName = "marker" }, new MemoryStream());
        try
        {
            Assert.Equal(0, item.Size);

            await using var stream = await bucket.OpenReadAsync(id);
            Assert.NotNull(stream);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            Assert.Equal(0, ms.Length);
        }
        finally
        {
            await bucket.DeleteOneAsync(id);
        }
    }

    [MinioFact]
    public async Task Object_NonSeekableStream_RoundTrip()
    {
        // A short non-seekable stream takes the simple path with memory bounded by one part.
        var bucket = fixture.Client.GetBucket<TestFileMetadata>();
        var id = Guid.NewGuid();
        var payload = new byte[100 * 1024];
        Random.Shared.NextBytes(payload);

        await bucket.UploadAsync(id, new TestFileMetadata { FileName = "pipe.bin" },
            new NonSeekableStream(new MemoryStream(payload)));
        try
        {
            var found = await bucket.FindOneAsync(id);
            Assert.NotNull(found);
            Assert.Equal(payload.Length, found.Size);
            Assert.Equal("pipe.bin", found.Metadata.FileName);

            Assert.Equal(payload, await ReadAllAsync(bucket, id));
        }
        finally
        {
            await bucket.DeleteOneAsync(id);
        }
    }

    [MinioTheory]
    [InlineData(1)]     // the default: one buffer, sequential
    [InlineData(3)]     // fewer buffers than parts, so each one is reused
    [InlineData(4)]
    [InlineData(8)]     // more buffers than parts, so some are never allocated
    public async Task Object_Multipart_RoundTrip(int parallelism)
    {
        // A connection of its own with shrunk thresholds (5 MB is the S3 minimum part size), so a 28 MB
        // payload becomes six parts on both the seekable and the non-seekable path. Fewer buffers than parts
        // is the case that matters: a buffer must not be refilled while its part is still on the wire, and
        // the manifest must be ordered by part number even though parts finish out of order.
        var services = new ServiceCollection();
        services.AddObjectStorage(o =>
        {
            MinioEnvironment.Apply(o);
            o.MultipartPartSize = 5 * 1024 * 1024;
            o.MultipartThreshold = 8 * 1024 * 1024;
            o.MultipartParallelism = parallelism;
        }).AddMapping<TestFileMetadata>(fixture.BucketName);

        await using var provider = services.BuildServiceProvider();
        var bucket = provider.GetRequiredService<IObjectStorageClient>().GetBucket<TestFileMetadata>();

        var payload = new byte[28 * 1024 * 1024];
        Random.Shared.NextBytes(payload);

        var seekableId = Guid.NewGuid();
        var pipeId = Guid.NewGuid();

        try
        {
            var uploaded = await bucket.UploadAsync(seekableId, new TestFileMetadata { FileName = "large.bin" },
                new MemoryStream(payload));
            await bucket.UploadAsync(pipeId, new TestFileMetadata { FileName = "large-pipe.bin" },
                new NonSeekableStream(new MemoryStream(payload)));

            Assert.Equal(payload.Length, uploaded.Size);

            // Metadata travels through InitiateMultipartUpload and must round-trip like the simple path.
            var found = await bucket.FindOneAsync(seekableId);
            Assert.NotNull(found);
            Assert.Equal(payload.Length, found.Size);
            Assert.Equal("large.bin", found.Metadata.FileName);

            Assert.Equal(payload, await ReadAllAsync(bucket, seekableId));
            Assert.Equal(payload, await ReadAllAsync(bucket, pipeId));
        }
        finally
        {
            await bucket.DeleteOneAsync(seekableId);
            await bucket.DeleteOneAsync(pipeId);
        }
    }

    [MinioFact]
    public async Task Object_Multipart_ReaderFails_AbortsAndReleasesItsBuffers()
    {
        // The failure path: several parts are in flight when the source dies. Buffer accounting is what is
        // under test — a buffer returned to the shared pool twice, or while a request still reads from it,
        // would corrupt a later unrelated upload, so the test ends by round-tripping a real payload.
        var services = new ServiceCollection();
        services.AddObjectStorage(o =>
        {
            MinioEnvironment.Apply(o);
            o.MultipartPartSize = 5 * 1024 * 1024;
            o.MultipartThreshold = 8 * 1024 * 1024;
            o.MultipartParallelism = 4;
        }).AddMapping<TestFileMetadata>(fixture.BucketName);

        await using var provider = services.BuildServiceProvider();
        var bucket = provider.GetRequiredService<IObjectStorageClient>().GetBucket<TestFileMetadata>();

        var payload = new byte[28 * 1024 * 1024];
        Random.Shared.NextBytes(payload);

        var brokenId = Guid.NewGuid();
        await Assert.ThrowsAsync<IOException>(() => bucket.UploadAsync(brokenId, new TestFileMetadata(),
            new FailingStream(new MemoryStream(payload), failAfterBytes: 18 * 1024 * 1024)));

        // Nothing was completed, so the object does not exist and the parts were abandoned.
        Assert.Null(await bucket.FindOneAsync(brokenId));

        var healthyId = Guid.NewGuid();
        await bucket.UploadAsync(healthyId, new TestFileMetadata { FileName = "after-failure.bin" },
            new MemoryStream(payload));
        try
        {
            Assert.Equal(payload, await ReadAllAsync(bucket, healthyId));
        }
        finally
        {
            await bucket.DeleteOneAsync(healthyId);
        }
    }

    static async Task<byte[]> ReadAllAsync<TMetadata>(IObjectBucket<TMetadata> bucket, Guid id)
        where TMetadata : class, IObjectMetadata
    {
        await using var stream = await bucket.OpenReadAsync(id);
        Assert.NotNull(stream);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    [MinioFact]
    public async Task Object_ServerSideCopy_BetweenBuckets()
    {
        // A second physical bucket, so the copy really crosses buckets and not just key prefixes.
        // Only the single-request branch is exercised here: the multipart copy needs an object above 5 GB,
        // which no integration run can produce — its partitioning is covered by S3CopyPartitioningTests.
        var targetBucketName = "it" + Guid.NewGuid().ToString("n");

        var services = new ServiceCollection();
        services.AddObjectStorage(MinioEnvironment.Apply)
            .AddMapping<TestFileMetadata>(fixture.BucketName)
            .AddMapping<ContextFileMetadata>(targetBucketName);

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IObjectStorageClient>();
        var source = client.GetBucket<TestFileMetadata>();
        var target = client.GetBucket<ContextFileMetadata>();

        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var payload = Encoding.UTF8.GetBytes("server-side copy");

        // Created last, so nothing between the creation and the finally that drops it can throw.
        await fixture.Client.CreateBucketAsync(targetBucketName);
        try
        {
            var uploaded = await source.UploadAsync(sourceId,
                new TestFileMetadata { FileName = "src.txt", ContentType = "text/plain" },
                new MemoryStream(payload), new UploadOptions { ContentType = "text/plain" });

            Assert.True(await source.CopyToAsync(sourceId, target, targetId, new ContextFileMetadata { FileName = "copy.txt" }));

            var copy = await target.FindOneAsync(targetId);
            Assert.NotNull(copy);
            Assert.Equal(uploaded.Size, copy.Size);
            Assert.Equal(uploaded.ETag, copy.ETag);         // identical bytes on a single-part object
            Assert.Equal("copy.txt", copy.Metadata.FileName);
            Assert.Equal(payload, await ReadAllAsync(target, targetId));

            // The metadata was replaced, not inherited: the source keeps its own, the copy carries only the new one.
            var original = await source.FindOneAsync(sourceId);
            Assert.NotNull(original);
            Assert.Equal("src.txt", original.Metadata.FileName);

            // No UploadOptions were passed to the copy, so the Content-Type travelled with it.
            using var http = new HttpClient();
            using var response = await http.GetAsync(await target.GetPresignedReadUrlAsync(targetId, TimeSpan.FromMinutes(5)));
            response.EnsureSuccessStatusCode();
            Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);

            // A missing source is a false, not an exception.
            Assert.False(await source.CopyToAsync(Guid.NewGuid(), target, Guid.NewGuid(), new ContextFileMetadata()));
        }
        finally
        {
            await source.DeleteOneAsync(sourceId);
            await target.DeleteOneAsync(targetId);
            await fixture.Client.DropBucketAsync(targetBucketName);
        }
    }

    [MinioFact]
    public async Task Object_ServerSideCopy_CarriesEveryHeaderOver()
    {
        // MetadataDirective.REPLACE wipes every system header, and UploadOptions can express only three of
        // them — but objects legitimately arrive carrying more through a presigned PUT, so a copy that
        // restored only those three would strip the rest. A gzip asset that lost its Content-Encoding
        // reaches the browser as unreadable bytes, exactly like a JPEG that lost its Content-Type.
        var bucket = fixture.Client.GetBucket<TestFileMetadata>();
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var payload = Encoding.UTF8.GetBytes("body { color: red }");
        using var http = new HttpClient();

        var writeUrl = await bucket.GetPresignedWriteUrlAsync(sourceId, TimeSpan.FromMinutes(5), "text/css");
        using (var content = new ByteArrayContent(payload))
        {
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/css");
            content.Headers.ContentEncoding.Add("gzip");
            content.Headers.ContentLanguage.Add("en");

            using var put = await http.PutAsync(writeUrl, content);
            put.EnsureSuccessStatusCode();
        }

        try
        {
            Assert.True(await bucket.CopyToAsync(sourceId, bucket, targetId,
                new TestFileMetadata { FileName = "copy.css" }));

            using var response = await http.GetAsync(await bucket.GetPresignedReadUrlAsync(targetId, TimeSpan.FromMinutes(5)));
            response.EnsureSuccessStatusCode();

            Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("gzip", response.Content.Headers.ContentEncoding);
            Assert.Contains("en", response.Content.Headers.ContentLanguage);
        }
        finally
        {
            await bucket.DeleteOneAsync(sourceId);
            await bucket.DeleteOneAsync(targetId);
        }
    }

    [MinioFact]
    public async Task Object_ServerSideCopy_OntoItself_RewritesMetadata()
    {
        // S3 refuses a copy onto the same key unless the request changes something; MetadataDirective.REPLACE
        // is what makes it legal, and this is the documented way to rewrite metadata in place.
        var bucket = fixture.Client.GetBucket<TestFileMetadata>();
        var id = Guid.NewGuid();
        var payload = Encoding.UTF8.GetBytes("in place");

        await bucket.UploadAsync(id, new TestFileMetadata { FileName = "old.txt" }, new MemoryStream(payload));
        try
        {
            Assert.True(await bucket.CopyToAsync(id, bucket, id, new TestFileMetadata { FileName = "new.txt" }));

            var item = await bucket.FindOneAsync(id);
            Assert.NotNull(item);
            Assert.Equal("new.txt", item.Metadata.FileName);
            Assert.Equal(payload, await ReadAllAsync(bucket, id));
        }
        finally
        {
            await bucket.DeleteOneAsync(id);
        }
    }

    [MinioFact]
    public async Task Object_ServerSideCopy_WithOptions_ReplacesTheSourceHeaders()
    {
        // Options are taken as a whole: the Cache-Control of the source must not survive into the copy.
        var bucket = fixture.Client.GetBucket<TestFileMetadata>();
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        await bucket.UploadAsync(sourceId, new TestFileMetadata(), new MemoryStream([1, 2, 3]),
            new UploadOptions { ContentType = "image/jpeg", CacheControl = "public, max-age=60" });
        try
        {
            Assert.True(await bucket.CopyToAsync(sourceId, bucket, targetId, new TestFileMetadata(),
                new UploadOptions { ContentType = "application/octet-stream" }));

            using var http = new HttpClient();
            using var response = await http.GetAsync(await bucket.GetPresignedReadUrlAsync(targetId, TimeSpan.FromMinutes(5)));
            response.EnsureSuccessStatusCode();

            Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
            Assert.Null(response.Headers.CacheControl);
        }
        finally
        {
            await bucket.DeleteOneAsync(sourceId);
            await bucket.DeleteOneAsync(targetId);
        }
    }

    [MinioFact]
    public async Task Object_ServerSideCopy_MissingTargetBucket_ThrowsNoSuchBucket()
    {
        var services = new ServiceCollection();
        services.AddObjectStorage(MinioEnvironment.Apply)
            .AddMapping<TestFileMetadata>(fixture.BucketName)
            .AddMapping<ContextFileMetadata>("it" + Guid.NewGuid().ToString("n"));   // never created

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IObjectStorageClient>();
        var source = client.GetBucket<TestFileMetadata>();

        var sourceId = Guid.NewGuid();
        await source.UploadAsync(sourceId, new TestFileMetadata(), new MemoryStream([1]));
        try
        {
            var ex = await Assert.ThrowsAsync<ObjectStorageException>(() => source.CopyToAsync(
                sourceId, client.GetBucket<ContextFileMetadata>(), Guid.NewGuid(), new ContextFileMetadata()));

            Assert.Equal("NoSuchBucket", ex.ErrorCode);
        }
        finally
        {
            await source.DeleteOneAsync(sourceId);
        }
    }

    [MinioFact]
    public async Task Storage_CopyAsync_CopiesThroughTheFacade()
    {
        // The IObjectStorageContext facade over the mapping-based client.
        var storage = fixture.Storage;
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var payload = Encoding.UTF8.GetBytes("through the facade");

        await storage.UploadAsync(sourceId, new TestFileMetadata { FileName = "src.txt" }, new MemoryStream(payload));
        try
        {
            Assert.True(await storage.CopyAsync<TestFileMetadata, ExtendedTestFileMetadata>(
                sourceId, targetId, new ExtendedTestFileMetadata { FileName = "copy.txt", Version = 2 }));

            var copy = await storage.FindAsync<ExtendedTestFileMetadata>(targetId);
            Assert.NotNull(copy);
            Assert.Equal("copy.txt", copy.Metadata.FileName);
            Assert.Equal(2, copy.Metadata.Version);
            Assert.Equal(payload.Length, copy.Size);

            Assert.False(await storage.CopyAsync<TestFileMetadata, ExtendedTestFileMetadata>(
                Guid.NewGuid(), Guid.NewGuid(), new ExtendedTestFileMetadata()));
        }
        finally
        {
            await storage.DeleteAsync<TestFileMetadata>(sourceId);
            await storage.DeleteAsync<ExtendedTestFileMetadata>(targetId);
        }
    }

    [MinioFact]
    public async Task Object_PresignedUrls_WorkOverPlainHttp()
    {
        var bucket = fixture.Client.GetBucket<TestFileMetadata>();
        var id = Guid.NewGuid();
        var payload = Encoding.UTF8.GetBytes("presigned payload");
        using var http = new HttpClient();

        // Read: upload with a content type, download via presigned GET — body and header must match.
        await bucket.UploadAsync(id, new TestFileMetadata { FileName = "p.txt" }, new MemoryStream(payload),
            new UploadOptions { ContentType = "text/plain" });
        try
        {
            var readUrl = await bucket.GetPresignedReadUrlAsync(id, TimeSpan.FromMinutes(5));
            using var response = await http.GetAsync(readUrl);
            response.EnsureSuccessStatusCode();

            Assert.Equal(payload, await response.Content.ReadAsByteArrayAsync());
            Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);

            // Write: the client uploads via presigned PUT; the object is then read through the library,
            // metadata is absent — properties come back as defaults (schema-tolerant read).
            var writeId = Guid.NewGuid();
            var writeUrl = await bucket.GetPresignedWriteUrlAsync(writeId, TimeSpan.FromMinutes(5), "text/plain");

            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            using var put = await http.PutAsync(writeUrl, content);
            put.EnsureSuccessStatusCode();

            var found = await bucket.FindOneAsync(writeId);
            Assert.NotNull(found);
            Assert.Equal(payload.Length, found.Size);
            Assert.Null(found.Metadata.FileName);

            await bucket.DeleteOneAsync(writeId);
        }
        finally
        {
            await bucket.DeleteOneAsync(id);
        }
    }

    [MinioFact]
    public async Task Object_List_ByMappingPrefix()
    {
        var files = fixture.Client.GetBucket<TestFileMetadata>();   // no mapping prefix
        var scoped = fixture.Context.Files;                         // mapping prefix "ctx" (fixture config)

        var fileId = Guid.NewGuid();
        var scopedId = Guid.NewGuid();
        await files.UploadAsync(fileId, new TestFileMetadata(), new MemoryStream([1]));
        await scoped.UploadAsync(scopedId, new ContextFileMetadata(), new MemoryStream([2, 2]));

        try
        {
            // The prefixed bucket sees only its own objects.
            var scopedItems = await scoped.ListAsync().ToListAsync();

            Assert.Contains(scopedItems, i => i.Key == $"ctx/{scopedId:d}" && i.Size == 2);
            Assert.DoesNotContain(scopedItems, i => i.Key.Contains($"{fileId:d}"));

            // The raw bucket sees everything.
            var all = await fixture.Client.GetBucket(fixture.BucketName).ListAsync().ToListAsync();

            Assert.Contains(all, i => i.Key == $"{fileId:d}");
            Assert.Contains(all, i => i.Key == $"ctx/{scopedId:d}");
        }
        finally
        {
            await files.DeleteOneAsync(fileId);
            await scoped.DeleteOneAsync(scopedId);
        }
    }

    [MinioFact]
    public async Task Object_Find_Missing_ReturnsNull()
    {
        var bucket = fixture.Client.GetBucket<TestFileMetadata>();
        Assert.Null(await bucket.FindOneAsync(Guid.NewGuid()));
    }

    [MinioFact]
    public async Task Context_BucketNameFromConfiguration_RoundTrip()
    {
        var context = fixture.Context;
        Assert.Equal(fixture.BucketName, context.Files.Name);
        Assert.True(await context.Files.ExistsAsync());

        var id = Guid.NewGuid();
        var payload = Encoding.UTF8.GetBytes("hello context");

        await context.Files.UploadAsync(id, new ContextFileMetadata { FileName = "ctx.txt" }, new MemoryStream(payload));
        try
        {
            var found = await context.Files.FindOneAsync(id);
            Assert.NotNull(found);
            Assert.Equal("ctx.txt", found!.Metadata.FileName);

            // Same bucket as the mapping-based tests, separated by the declared key prefix.
            Assert.Null(await fixture.Client.GetBucket<TestFileMetadata>().FindOneAsync(id));
        }
        finally
        {
            await context.Files.DeleteOneAsync(id);
        }
    }

    private record Payload(int Number, string Text);
}
