namespace BrandUp.Extensions.ObjectStorage;

public class FakeObjectStorageClientTests
{
    readonly FakeObjectStore _store = new();
    readonly FakeObjectStorageClient _client;

    public FakeObjectStorageClientTests()
    {
        _client = new FakeObjectStorageClient(_store);
        _client.AddMapping<DocMetadata>("docs/files");
    }

    [Fact]
    public async Task CreateBucketAsync_MakesBucketExist()
    {
        await _client.CreateBucketAsync("newbucket");
        Assert.True(_store.BucketExists("newbucket"));
    }

    [Fact]
    public async Task CreateBucketAsync_WithSettings_AppliesSettings()
    {
        await _client.CreateBucketAsync("mybucket", s => s.Versioning = BucketVersioning.Enabled);
        var settings = await _client.GetBucket("mybucket").GetSettingsAsync();
        Assert.Equal(BucketVersioning.Enabled, settings.Versioning);
    }

    [Fact]
    public async Task DropBucketAsync_RemovesBucket()
    {
        await _client.CreateBucketAsync("temp");
        await _client.DropBucketAsync("temp");
        Assert.False(_store.BucketExists("temp"));
    }

    [Fact]
    public async Task ListBucketsAsync_ReturnsCreatedBuckets()
    {
        await _client.CreateBucketAsync("alpha");
        await _client.CreateBucketAsync("beta");
        var list = await _client.ListBucketsAsync();
        Assert.Contains(list, b => b.Name == "alpha");
        Assert.Contains(list, b => b.Name == "beta");
    }

    [Fact]
    public void GetBucket_ByString_ReturnsUntypedBucket()
    {
        _store.CreateBucket("raw");
        var bucket = _client.GetBucket("raw");
        Assert.Equal("raw", bucket.Name);
    }

    [Fact]
    public void GetBucket_Generic_ReturnsTypedBucket()
    {
        var bucket = _client.GetBucket<DocMetadata>();
        Assert.Equal("docs", bucket.Name);
    }

    [Fact]
    public void GetBucket_Generic_ThrowsForUnregistered()
    {
        Assert.Throws<InvalidOperationException>(() => _client.GetBucket<UnregisteredMetadata>());
    }

    private class DocMetadata : IObjectMetadata { }
    private class UnregisteredMetadata : IObjectMetadata { }
}
