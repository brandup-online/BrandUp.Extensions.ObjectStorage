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
    [InlineData("bucket name")]   // пробел
    [InlineData("bucket@name")]   // @
    [InlineData("bucket_name")]   // подчёркивание
    public void AddMapping_ThrowsForInvalidChars(string destination)
    {
        Assert.Throws<ArgumentException>(() => _builder.AddMapping<TestMetadata>(destination));
    }

    [Fact]
    public void AddMapping_ThrowsForConsecutiveSlashes()
    {
        Assert.Throws<ArgumentException>(() => _builder.AddMapping<TestMetadata>("bucket//prefix"));
    }

    [Fact]
    public void AddMapping_ThrowsForConsecutiveDots()
    {
        Assert.Throws<ArgumentException>(() => _builder.AddMapping<TestMetadata>("my..bucket"));
    }

    [Theory]
    [InlineData("-bucket")]
    [InlineData(".bucket")]
    public void AddMapping_ThrowsWhenBucketNameStartsWithDashOrDot(string destination)
    {
        Assert.Throws<ArgumentException>(() => _builder.AddMapping<TestMetadata>(destination));
    }

    [Theory]
    [InlineData("bucket-")]
    [InlineData("bucket.")]
    public void AddMapping_ThrowsWhenBucketNameEndsWithDashOrDot(string destination)
    {
        Assert.Throws<ArgumentException>(() => _builder.AddMapping<TestMetadata>(destination));
    }

    [Theory]
    [InlineData("/bucket")]
    [InlineData("bucket/")]
    [InlineData("  bucket  ")]
    public void AddMapping_NormalizesDestination(string destination)
    {
        var result = _builder.AddMapping<TestMetadata2>(destination);
        Assert.Same(_builder, result);
    }

    [Theory]
    [InlineData("mybucket")]
    [InlineData("mybucket/prefix")]
    [InlineData("mybucket/sub/prefix")]
    [InlineData("bucket-name")]
    [InlineData("bucket.name")]
    [InlineData("my-bucket.v2/some-prefix")]
    public void AddMapping_ValidDestination_ReturnsBuilder(string destination)
    {
        var result = _builder.AddMapping<TestMetadata3>(destination);
        Assert.Same(_builder, result);
    }

    [Fact]
    public void AddMapping_SameTypeMultipleTimes_LastWins()
    {
        _builder.AddMapping<TestMetadata4>("bucket1");
        var result = _builder.AddMapping<TestMetadata4>("bucket2");
        Assert.Same(_builder, result);
    }

    // Отдельные классы метаданных, чтобы тесты не мешали друг другу при регистрации в одном builder'е
    private class TestMetadata : IObjectMetadata { }
    private class TestMetadata2 : IObjectMetadata { }
    private class TestMetadata3 : IObjectMetadata { }
    private class TestMetadata4 : IObjectMetadata { }
}
