namespace BrandUp.Extensions.ObjectStorage;

public class FakeObjectBucketTests
{
    readonly FakeObjectStore _store = new();
    readonly FakeObjectBucket<FileMetadata> _bucket;

    public FakeObjectBucketTests()
    {
        _store.CreateBucket("files");
        _bucket = new FakeObjectBucket<FileMetadata>("files", "docs", _store);
    }

    [Fact]
    public async Task ExistsAsync_ReturnsTrue_ForCreatedBucket()
    {
        Assert.True(await _bucket.ExistsAsync());
    }

    [Fact]
    public async Task ExistsAsync_ReturnsFalse_ForMissingBucket()
    {
        var bucket = new FakeObjectBucket<FileMetadata>("missing", null, _store);
        Assert.False(await bucket.ExistsAsync());
    }

    [Fact]
    public async Task FindOneAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _bucket.FindOneAsync(Guid.NewGuid());
        Assert.Null(result);
    }

    [Fact]
    public async Task UploadAsync_ThenFindOne_ReturnsItem()
    {
        var id = Guid.NewGuid();
        var metadata = new FileMetadata { Name = "doc.pdf", Size = 512 };
        using var stream = new MemoryStream([1, 2, 3, 4]);

        await _bucket.UploadAsync(id, metadata, stream);
        var item = await _bucket.FindOneAsync(id);

        Assert.NotNull(item);
        Assert.Equal(id, item.Id);
        Assert.Equal(4, item.Size);
        Assert.Equal("doc.pdf", item.Metadata.Name);
        Assert.Equal(512, item.Metadata.Size);
        Assert.NotNull(item.ETag);
    }

    [Fact]
    public async Task OpenReadAsync_ReturnsCorrectBytes()
    {
        var id = Guid.NewGuid();
        var content = new byte[] { 10, 20, 30 };
        await _bucket.UploadAsync(id, new FileMetadata(), new MemoryStream(content));

        await using var stream = await _bucket.OpenReadAsync(id);
        Assert.NotNull(stream);

        var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        Assert.Equal(content, buffer.ToArray());
    }

    [Fact]
    public async Task OpenReadAsync_ReturnsNull_WhenNotFound()
    {
        var stream = await _bucket.OpenReadAsync(Guid.NewGuid());
        Assert.Null(stream);
    }

    [Fact]
    public async Task DeleteOneAsync_ReturnsTrue_WhenExists()
    {
        var id = Guid.NewGuid();
        await _bucket.UploadAsync(id, new FileMetadata(), new MemoryStream([1]));
        Assert.True(await _bucket.DeleteOneAsync(id));
    }

    [Fact]
    public async Task DeleteOneAsync_ReturnsFalse_WhenNotFound()
    {
        Assert.False(await _bucket.DeleteOneAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task DeleteOneAsync_ThenFindOne_ReturnsNull()
    {
        var id = Guid.NewGuid();
        await _bucket.UploadAsync(id, new FileMetadata(), new MemoryStream([1]));
        await _bucket.DeleteOneAsync(id);
        Assert.Null(await _bucket.FindOneAsync(id));
    }

    [Fact]
    public async Task Upload_SameId_Overwrites()
    {
        var id = Guid.NewGuid();
        await _bucket.UploadAsync(id, new FileMetadata { Name = "v1" }, new MemoryStream([1]));
        await _bucket.UploadAsync(id, new FileMetadata { Name = "v2" }, new MemoryStream([2, 3]));

        var item = await _bucket.FindOneAsync(id);
        Assert.NotNull(item);
        Assert.Equal("v2", item.Metadata.Name);
        Assert.Equal(2, item.Size);
    }

    [Fact]
    public async Task GetSettingsAsync_ReturnsDefaultSettings()
    {
        var settings = await _bucket.GetSettingsAsync();
        Assert.Equal(BucketAccess.Private, settings.Access);
        Assert.Equal(BucketVersioning.Disabled, settings.Versioning);
    }

    [Fact]
    public async Task UpdateSettingsAsync_PersistsChanges()
    {
        await _bucket.UpdateSettingsAsync(s => s.Versioning = BucketVersioning.Enabled);
        var settings = await _bucket.GetSettingsAsync();
        Assert.Equal(BucketVersioning.Enabled, settings.Versioning);
    }

    private class FileMetadata : IObjectMetadata
    {
        public string Name { get; set; } = "";
        public int Size { get; set; }
    }
}
