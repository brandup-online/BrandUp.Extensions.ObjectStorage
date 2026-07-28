using BrandUp.Extensions.ObjectStorage.Internals;

namespace BrandUp.Extensions.ObjectStorage;

public class BucketResolverTests
{
    static Dictionary<string, string> Buckets(params (string Key, string Value)[] entries)
        => entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);

    #region Context path (name required)

    [Fact]
    public void Resolve_ConfiguredName()
    {
        var result = BucketResolver.Resolve("Photos", Buckets(("Photos", "prod-photos")),
            namePrefix: null, nameSuffix: null, fallbackDestination: null, "Ctx.Photos");

        Assert.Equal("prod-photos", result);
    }

    [Fact]
    public void Resolve_ConfiguredNameWithPrefix()
    {
        var result = BucketResolver.Resolve("Photos", Buckets(("Photos", "prod-photos/2024/raw")),
            namePrefix: null, nameSuffix: null, fallbackDestination: null, "Ctx.Photos");

        Assert.Equal("prod-photos/2024/raw", result);
    }

    [Fact]
    public void Resolve_NamePrefixAndSuffix_WrapNameOnly()
    {
        var result = BucketResolver.Resolve("Photos", Buckets(("Photos", "photos/raw")),
            namePrefix: "acme-", nameSuffix: "-dev", fallbackDestination: null, "Ctx.Photos");

        Assert.Equal("acme-photos-dev/raw", result);
    }

    [Fact]
    public void Resolve_NotConfigured_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BucketResolver.Resolve(
            "Photos", Buckets(), null, null, fallbackDestination: null, "Ctx.Photos"));

        Assert.Contains("Objects:Photos", ex.Message);
    }

    [Fact]
    public void Resolve_KeyLookupIsCaseInsensitive()
    {
        var result = BucketResolver.Resolve("Photos", Buckets(("PHOTOS", "prod-photos")),
            null, null, fallbackDestination: null, "Ctx.Photos");

        Assert.Equal("prod-photos", result);
    }

    #endregion

    #region Mapping path (name declared in code)

    [Fact]
    public void ResolveDestination_NotConfigured_KeepsDeclared()
    {
        var result = BucketResolver.ResolveDestination("legacy/items", Buckets(), null, null, "LegacyMetadata");

        Assert.Equal("legacy/items", result);
    }

    [Fact]
    public void ResolveDestination_ConfiguredName_KeepsDeclaredPrefix()
    {
        var result = BucketResolver.ResolveDestination(
            "legacy/items", Buckets(("legacy", "prod-legacy")), null, null, "LegacyMetadata");

        Assert.Equal("prod-legacy/items", result);
    }

    [Fact]
    public void ResolveDestination_ConfiguredPrefix_OverridesDeclared()
    {
        var result = BucketResolver.ResolveDestination(
            "legacy/items", Buckets(("legacy", "prod-legacy/archive")), null, null, "LegacyMetadata");

        Assert.Equal("prod-legacy/archive", result);
    }

    [Fact]
    public void ResolveDestination_TwoMappingsOfOneBucket_KeepOwnPrefixes()
    {
        var buckets = Buckets(("docs", "prod-docs"));

        Assert.Equal("prod-docs/a", BucketResolver.ResolveDestination("docs/a", buckets, null, null, "A"));
        Assert.Equal("prod-docs/b", BucketResolver.ResolveDestination("docs/b", buckets, null, null, "B"));
    }

    [Fact]
    public void ResolveDestination_NameSuffix_Applied()
    {
        var result = BucketResolver.ResolveDestination("legacy/items", Buckets(), null, "-dev", "LegacyMetadata");

        Assert.Equal("legacy-dev/items", result);
    }

    #endregion
}
