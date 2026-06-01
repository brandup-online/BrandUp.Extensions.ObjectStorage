namespace BrandUp.Extensions.ObjectStorage;

public class ObjectItemTests
{
    [Fact]
    public void Properties_AreInitializable()
    {
        var metadata = new TestMetadata { Name = "photo.jpg" };
        var item = new ObjectItem<TestMetadata>
        {
            Id       = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Size     = 1024,
            ETag     = "\"abc123\"",
            Metadata = metadata
        };

        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), item.Id);
        Assert.Equal(1024, item.Size);
        Assert.Equal("\"abc123\"", item.ETag);
        Assert.Same(metadata, item.Metadata);
    }

    [Fact]
    public void ETag_CanBeNull()
    {
        var item = new ObjectItem<TestMetadata> { Metadata = new TestMetadata() };
        Assert.Null(item.ETag);
    }

    private class TestMetadata : IObjectMetadata
    {
        public string Name { get; set; } = "";
    }
}
