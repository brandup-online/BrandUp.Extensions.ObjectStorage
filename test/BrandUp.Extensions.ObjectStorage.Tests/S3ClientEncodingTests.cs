using BrandUp.Extensions.ObjectStorage.Internals;

namespace BrandUp.Extensions.ObjectStorage;

public class S3ClientEncodingTests
{
    [Theory]
    [InlineData("FileName",    "file-name")]
    [InlineData("ContentType", "content-type")]
    [InlineData("UploadedAt",  "uploaded-at")]
    [InlineData("myProperty",  "my-property")]
    [InlineData("XMLParser",   "xml-parser")]
    [InlineData("simplekey",   "simplekey")]
    public void EncodeMetadataKey_ConvertsToTrainCase(string input, string expected)
    {
        Assert.Equal(expected, S3Client.EncodeMetadataKey(input));
    }

    [Fact]
    public void EncodeMetadataKey_ThrowsForNull()
    {
        Assert.ThrowsAny<ArgumentException>(() => S3Client.EncodeMetadataKey(null!));
    }

    [Fact]
    public void EncodeMetadataKey_ThrowsForEmpty()
    {
        Assert.Throws<ArgumentException>(() => S3Client.EncodeMetadataKey(""));
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("привет мир")]
    [InlineData("special chars: !@#$%")]
    [InlineData("")]
    public void EncodeDecodeValue_RoundTrip(string value)
    {
        var encoded = S3Client.EncodeMetadataValue(value);
        var decoded = S3Client.DecodeMetadataValue(encoded);
        Assert.Equal(value, decoded);
    }

    [Fact]
    public void EncodeMetadataValue_ProducesHexString()
    {
        var encoded = S3Client.EncodeMetadataValue("test");
        Assert.Matches("^[0-9A-F]+$", encoded);
    }

    [Fact]
    public void DecodeMetadataValue_NonHex_ReturnsOriginal()
    {
        // fallback for legacy values not in hex format
        var result = S3Client.DecodeMetadataValue("plaintext-value");
        Assert.Equal("plaintext-value", result);
    }
}
