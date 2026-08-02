using System.Text;
using BrandUp.Extensions.ObjectStorage.Internals;

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

    [MinioFact]
    public async Task Object_Multipart_RoundTrip()
    {
        // Shrink the thresholds (5 MB is the S3 minimum part size) so multipart triggers on a 12 MB payload
        // for both the large-seekable and the long-non-seekable path.
        var (partSize, threshold) = (S3Client.MultipartPartSize, S3Client.MultipartThreshold);
        S3Client.MultipartPartSize = 5 * 1024 * 1024;
        S3Client.MultipartThreshold = 8 * 1024 * 1024;
        try
        {
            var bucket = fixture.Client.GetBucket<TestFileMetadata>();
            var payload = new byte[12 * 1024 * 1024];
            Random.Shared.NextBytes(payload);

            var seekableId = Guid.NewGuid();
            var pipeId = Guid.NewGuid();

            var uploaded = await bucket.UploadAsync(seekableId, new TestFileMetadata { FileName = "large.bin" },
                new MemoryStream(payload));
            await bucket.UploadAsync(pipeId, new TestFileMetadata { FileName = "large-pipe.bin" },
                new NonSeekableStream(new MemoryStream(payload)));
            try
            {
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
        finally
        {
            (S3Client.MultipartPartSize, S3Client.MultipartThreshold) = (partSize, threshold);
        }
    }

    static async Task<byte[]> ReadAllAsync(IObjectBucket<TestFileMetadata> bucket, Guid id)
    {
        await using var stream = await bucket.OpenReadAsync(id);
        Assert.NotNull(stream);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    [MinioFact]
    public async Task Object_PresignedUrls_WorkOverPlainHttp()
    {
        var bucket = fixture.Client.GetBucket<TestFileMetadata>();
        var id = Guid.NewGuid();
        var payload = Encoding.UTF8.GetBytes("presigned payload");
        using var http = new HttpClient();

        // Read: upload with a content type, download через presigned GET — тело и заголовок совпадают.
        await bucket.UploadAsync(id, new TestFileMetadata { FileName = "p.txt" }, new MemoryStream(payload),
            new UploadOptions { ContentType = "text/plain" });
        try
        {
            var readUrl = await bucket.GetPresignedReadUrlAsync(id, TimeSpan.FromMinutes(5));
            using var response = await http.GetAsync(readUrl);
            response.EnsureSuccessStatusCode();

            Assert.Equal(payload, await response.Content.ReadAsByteArrayAsync());
            Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);

            // Write: клиент заливает по presigned PUT; объект читается библиотекой,
            // метаданные отсутствуют — свойства приходят дефолтными (schema-tolerant чтение).
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
            var scopedItems = new List<ObjectListItem>();
            await foreach (var item in scoped.ListAsync())
                scopedItems.Add(item);

            Assert.Contains(scopedItems, i => i.Key == $"ctx/{scopedId:d}" && i.Size == 2);
            Assert.DoesNotContain(scopedItems, i => i.Key.Contains($"{fileId:d}"));

            // The raw bucket sees everything.
            var all = new List<ObjectListItem>();
            await foreach (var item in fixture.Client.GetBucket(fixture.BucketName).ListAsync())
                all.Add(item);

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
