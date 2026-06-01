using Microsoft.Extensions.DependencyInjection;

namespace BrandUp.Extensions.ObjectStorage;

public class FakeObjectStorageIntegrationTests
{
    readonly IObjectStorage _storage;
    readonly IObjectStorageClient _client;
    readonly IObjectBucket<FileMetadata> _bucket;
    readonly FakeObjectStore _store;

    public FakeObjectStorageIntegrationTests()
    {
        var services = new ServiceCollection();
        services.AddFakeObjectStorage()
            .AddMapping<FileMetadata>("files/docs")
            .WithBucket("files");

        var sp = services.BuildServiceProvider();
        _storage = sp.GetRequiredService<IObjectStorage>();
        _client  = sp.GetRequiredService<IObjectStorageClient>();
        _bucket  = sp.GetRequiredService<IObjectBucket<FileMetadata>>();
        _store   = sp.GetRequiredService<FakeObjectStore>();
    }

    // IObjectStorage tests

    [Fact]
    public async Task Storage_Upload_Find_ReturnsMetadata()
    {
        var id = Guid.NewGuid();
        var metadata = new FileMetadata { Name = "report.pdf" };

        await _storage.UploadAsync(id, metadata, new MemoryStream([1, 2, 3]));
        var item = await _storage.FindAsync<FileMetadata>(id);

        Assert.NotNull(item);
        Assert.Equal(id, item.Id);
        Assert.Equal("report.pdf", item.Metadata.Name);
        Assert.Equal(3, item.Size);
    }

    [Fact]
    public async Task Storage_Find_ReturnsNull_WhenNotFound()
    {
        Assert.Null(await _storage.FindAsync<FileMetadata>(Guid.NewGuid()));
    }

    [Fact]
    public async Task Storage_Read_ReturnsContent()
    {
        var id = Guid.NewGuid();
        var bytes = "hello world"u8.ToArray();
        await _storage.UploadAsync(id, new FileMetadata(), new MemoryStream(bytes));

        await using var stream = await _storage.ReadAsync<FileMetadata>(id);
        Assert.NotNull(stream);

        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        Assert.Equal(bytes, ms.ToArray());
    }

    [Fact]
    public async Task Storage_Read_ReturnsNull_WhenNotFound()
    {
        Assert.Null(await _storage.ReadAsync<FileMetadata>(Guid.NewGuid()));
    }

    [Fact]
    public async Task Storage_Delete_ReturnsTrue_AndRemovesObject()
    {
        var id = Guid.NewGuid();
        await _storage.UploadAsync(id, new FileMetadata(), new MemoryStream([1]));

        Assert.True(await _storage.DeleteAsync<FileMetadata>(id));
        Assert.Null(await _storage.FindAsync<FileMetadata>(id));
    }

    [Fact]
    public async Task Storage_Delete_ReturnsFalse_WhenNotFound()
    {
        Assert.False(await _storage.DeleteAsync<FileMetadata>(Guid.NewGuid()));
    }

    [Fact]
    public async Task Storage_ETag_IsConsistentForSameContent()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        await _storage.UploadAsync(id1, new FileMetadata(), new MemoryStream(bytes));
        await _storage.UploadAsync(id2, new FileMetadata(), new MemoryStream(bytes));

        var item1 = await _storage.FindAsync<FileMetadata>(id1);
        var item2 = await _storage.FindAsync<FileMetadata>(id2);

        Assert.Equal(item1!.ETag, item2!.ETag);
    }

    // IObjectBucket<T> tests (injected directly)

    [Fact]
    public async Task Bucket_UploadAndFindOne_WorksViaDIInjectedBucket()
    {
        var id = Guid.NewGuid();
        await _bucket.UploadAsync(id, new FileMetadata { Name = "via-bucket.txt" }, new MemoryStream([99]));
        var item = await _bucket.FindOneAsync(id);
        Assert.NotNull(item);
        Assert.Equal("via-bucket.txt", item.Metadata.Name);
    }

    // FakeObjectStore inspection

    [Fact]
    public async Task Store_TracksObjectCount()
    {
        await _storage.UploadAsync(Guid.NewGuid(), new FileMetadata(), new MemoryStream([1]));
        await _storage.UploadAsync(Guid.NewGuid(), new FileMetadata(), new MemoryStream([2]));
        Assert.Equal(2, _store.GetObjectCount("files"));
    }

    [Fact]
    public void Store_BucketPreCreatedByWithBucket()
    {
        Assert.True(_store.BucketExists("files"));
    }

    // IObjectStorageClient bucket management

    [Fact]
    public async Task Client_CreateAndDropBucket()
    {
        await _client.CreateBucketAsync("temporary");
        Assert.True(_store.BucketExists("temporary"));

        await _client.DropBucketAsync("temporary");
        Assert.False(_store.BucketExists("temporary"));
    }

    [Fact]
    public async Task Client_ListBuckets_IncludesPreCreatedBucket()
    {
        var buckets = await _client.ListBucketsAsync();
        Assert.Contains(buckets, b => b.Name == "files");
    }

    private class FileMetadata : IObjectMetadata
    {
        public string Name { get; set; } = "";
    }
}
