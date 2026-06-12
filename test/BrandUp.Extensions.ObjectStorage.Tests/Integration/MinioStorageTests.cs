using System.Text;

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
    public async Task Object_Find_Missing_ReturnsNull()
    {
        var bucket = fixture.Client.GetBucket<TestFileMetadata>();
        Assert.Null(await bucket.FindOneAsync(Guid.NewGuid()));
    }

    private record Payload(int Number, string Text);
}
