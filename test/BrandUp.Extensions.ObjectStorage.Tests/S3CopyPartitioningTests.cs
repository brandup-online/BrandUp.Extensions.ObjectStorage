using BrandUp.Extensions.ObjectStorage.Internals;

namespace BrandUp.Extensions.ObjectStorage;

/// <summary>
/// The multipart branch of a server-side copy needs an object above 5 GB to trigger, so it is not covered
/// by the MinIO integration suite: the partitioning it relies on is checked here instead.
/// </summary>
public class S3CopyPartitioningTests
{
    const int DefaultPartSize = 16 * 1024 * 1024;

    /// <summary>Just above the single-copy limit, 100 GB, and the 5 TB S3 object size limit.</summary>
    public static TheoryData<long> LargeObjects =>
    [
        S3Client.MaxSinglePutSize + 1,
        100L * 1024 * 1024 * 1024,
        S3Client.MaxObjectSize
    ];

    [Theory]
    [MemberData(nameof(LargeObjects))]
    public void CopyPartRanges_CoverTheObjectExactlyAndFitTheS3PartLimit(long contentLength)
    {
        var partSize = S3Client.ComputePartSize(contentLength, DefaultPartSize);
        var ranges = S3Client.CopyPartRanges(contentLength, partSize).ToList();

        Assert.InRange(ranges.Count, 1, S3Client.MaxParts);
        Assert.Equal(0, ranges[0].FirstByte);
        Assert.Equal(contentLength - 1, ranges[^1].LastByte);

        for (var i = 0; i < ranges.Count; i++)
        {
            Assert.Equal(i + 1, ranges[i].PartNumber);              // S3 numbers parts from 1

            // S3 rejects any part below 5 MB except the last one, so an undersized range would fail the
            // whole copy at CompleteMultipartUpload — the constraint the part size has to satisfy.
            var length = ranges[i].LastByte - ranges[i].FirstByte + 1;
            Assert.True(length > 0);
            if (i < ranges.Count - 1)
                Assert.True(length >= 5 * 1024 * 1024, $"part {i + 1} is {length} bytes");

            if (i > 0)
                Assert.Equal(ranges[i - 1].LastByte + 1, ranges[i].FirstByte);   // contiguous, no overlap
        }

        Assert.Equal(contentLength, ranges.Sum(r => r.LastByte - r.FirstByte + 1));
    }

    [Fact]
    public void CopyPartRanges_LastPartCarriesTheRemainder()
    {
        var ranges = S3Client.CopyPartRanges(25, partSize: 10).ToList();

        Assert.Equal(3, ranges.Count);
        Assert.Equal((1, 0L, 9L), (ranges[0].PartNumber, ranges[0].FirstByte, ranges[0].LastByte));
        Assert.Equal((2, 10L, 19L), (ranges[1].PartNumber, ranges[1].FirstByte, ranges[1].LastByte));
        Assert.Equal((3, 20L, 24L), (ranges[2].PartNumber, ranges[2].FirstByte, ranges[2].LastByte));
    }

    [Fact]
    public void CopyPartRanges_SinglePartWhenTheObjectFitsOneRange()
    {
        var ranges = S3Client.CopyPartRanges(4 * 1024 * 1024, partSize: 5 * 1024 * 1024).ToList();

        var single = Assert.Single(ranges);
        Assert.Equal((1, 0L, 4L * 1024 * 1024 - 1), (single.PartNumber, single.FirstByte, single.LastByte));
    }
}
