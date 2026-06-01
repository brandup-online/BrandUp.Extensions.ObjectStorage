namespace BrandUp.Extensions.ObjectStorage;

public class FakeObjectStoreTests
{
    readonly FakeObjectStore _store = new();

    [Fact]
    public void BucketExists_ReturnsFalse_ForNew()
    {
        Assert.False(_store.BucketExists("nonexistent"));
    }

    [Fact]
    public void CreateBucket_MakesBucketExist()
    {
        _store.CreateBucket("test");
        Assert.True(_store.BucketExists("test"));
    }

    [Fact]
    public void CreateBucket_Duplicate_Throws()
    {
        _store.CreateBucket("test");
        Assert.Throws<InvalidOperationException>(() => _store.CreateBucket("test"));
    }

    [Fact]
    public void DropBucket_RemovesBucket()
    {
        _store.CreateBucket("test");
        _store.DropBucket("test");
        Assert.False(_store.BucketExists("test"));
    }

    [Fact]
    public void GetBucketNames_ReturnsAll()
    {
        _store.CreateBucket("a");
        _store.CreateBucket("b");
        var names = _store.GetBucketNames();
        Assert.Contains("a", names);
        Assert.Contains("b", names);
    }

    [Fact]
    public void GetObjectCount_ReturnsZero_BeforeUploads()
    {
        _store.CreateBucket("empty");
        Assert.Equal(0, _store.GetObjectCount("empty"));
    }

    [Fact]
    public void GetObjectCount_AfterPut_ReturnsOne()
    {
        _store.CreateBucket("bucket");
        _store.PutObject("bucket", "key1", [1, 2, 3], new object());
        Assert.Equal(1, _store.GetObjectCount("bucket"));
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        _store.CreateBucket("a");
        _store.CreateBucket("b");
        _store.Clear();
        Assert.Empty(_store.GetBucketNames());
    }
}
