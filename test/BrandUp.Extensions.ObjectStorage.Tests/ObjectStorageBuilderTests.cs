using Microsoft.Extensions.DependencyInjection;

namespace BrandUp.Extensions.ObjectStorage;

public class ObjectStorageBuilderTests
{
    readonly ObjectStorageBuilder _builder = new(new ServiceCollection());

    [Fact]
    public void AddMapping_ThrowsForNull()
    {
        Assert.ThrowsAny<ArgumentException>(() => _builder.AddMapping<TestMetadata>(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddMapping_ThrowsForEmptyOrWhitespace(string destination)
    {
        Assert.Throws<ArgumentException>(() => _builder.AddMapping<TestMetadata>(destination));
    }

    [Theory]
    [InlineData("bucket-name")]
    [InlineData("bucket name")]
    [InlineData("bucket@name")]
    [InlineData("bucket.name")]
    public void AddMapping_ThrowsForInvalidChars(string destination)
    {
        Assert.Throws<ArgumentException>(() => _builder.AddMapping<TestMetadata>(destination));
    }

    [Fact]
    public void AddMapping_ThrowsForConsecutiveSlashes()
    {
        Assert.Throws<ArgumentException>(() => _builder.AddMapping<TestMetadata>("bucket//prefix"));
    }

    [Theory]
    [InlineData("/bucket")]
    [InlineData("bucket/")]
    [InlineData("  bucket  ")]
    public void AddMapping_NormalizesDestination(string destination)
    {
        // trimming border slashes and whitespace should succeed
        var result = _builder.AddMapping<TestMetadata>(destination);
        Assert.Same(_builder, result);
    }

    [Theory]
    [InlineData("mybucket")]
    [InlineData("mybucket/prefix")]
    [InlineData("mybucket/sub/prefix")]
    public void AddMapping_ValidDestination_ReturnsBuilder(string destination)
    {
        var result = _builder.AddMapping<TestMetadata>(destination);
        Assert.Same(_builder, result);
    }

    [Fact]
    public void AddMapping_SameTypeMultipleTimes_LastWins()
    {
        _builder.AddMapping<TestMetadata>("bucket1");
        var result = _builder.AddMapping<TestMetadata>("bucket2");
        Assert.Same(_builder, result);
    }

    private class TestMetadata : IObjectMetadata { }
}
