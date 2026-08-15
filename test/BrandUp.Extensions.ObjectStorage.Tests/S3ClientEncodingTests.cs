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
    [InlineData("DEAD")]        // valid hex both upper- and lowercase — must survive the round trip
    [InlineData("cafe")]
    [InlineData("25")]          // even-length digits parse as hex too
    [InlineData("  padded  ")]  // outer spaces would be trimmed by HTTP
    [InlineData("photo.jpg")]
    public void EncodeDecodeValue_RoundTrip(string value)
    {
        var encoded = S3Client.EncodeMetadataValue(value);
        var decoded = S3Client.DecodeMetadataValue(encoded);
        Assert.Equal(value, decoded);
    }

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("text/plain")]
    [InlineData("special chars: !@#$%")]
    [InlineData("123")]   // odd length — the hex decoder rejects it, so plaintext is safe
    public void EncodeMetadataValue_AsciiSafeValue_PassesThroughAsIs(string value)
    {
        Assert.Equal(value, S3Client.EncodeMetadataValue(value));
    }

    [Theory]
    [InlineData("пример.txt")]   // non-ASCII
    [InlineData("DEAD")]         // hex look-alike: written as-is it would be hex-decoded on read
    [InlineData("25")]
    [InlineData(" padded")]      // outer space would not survive an HTTP header
    [InlineData("tab\there")]    // control character
    [InlineData("a  b")]         // consecutive spaces may be collapsed by header-normalizing hops
    public void EncodeMetadataValue_UnsafeOrAmbiguousValue_IsHexEncoded(string value)
    {
        var encoded = S3Client.EncodeMetadataValue(value);
        Assert.NotEqual(value, encoded);
        Assert.Matches("^[0-9A-F]*$", encoded);
    }

    [Fact]
    public void DecodeMetadataValue_NonHex_ReturnsOriginal()
    {
        // fallback for legacy values not in hex format
        var result = S3Client.DecodeMetadataValue("plaintext-value");
        Assert.Equal("plaintext-value", result);
    }

    [Fact]
    public void ComputePartSize_ScalesToFitTenThousandParts()
    {
        var defaultPartSize = new ObjectStorageOptions().MultipartPartSize;

        // Small payloads keep the default part; a 5 TB payload needs ~550 MB parts to fit 10 000.
        Assert.Equal(defaultPartSize, S3Client.ComputePartSize(100L * 1024 * 1024, defaultPartSize));

        var part = S3Client.ComputePartSize(S3Client.MaxObjectSize, defaultPartSize);
        Assert.True((long)part * S3Client.MaxParts >= S3Client.MaxObjectSize);
        Assert.True(part < 600 * 1024 * 1024);
    }
}
