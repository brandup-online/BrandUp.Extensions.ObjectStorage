using BrandUp.Extensions.ObjectStorage.Internals;

namespace BrandUp.Extensions.ObjectStorage;

public class StorageModelTests
{
    [Fact]
    public void Build_ReadsConfigurationKey()
    {
        var model = StorageModel.Build(typeof(ValidContext));

        Assert.Equal(2, model.Properties.Count);

        // No key declared — the property name is the configuration key.
        var photos = model.Properties.Single(p => p.MetadataType == typeof(PhotoMetadata));
        Assert.Equal(nameof(ValidContext.Photos), photos.ConfigurationKey);

        var videos = model.Properties.Single(p => p.MetadataType == typeof(VideoMetadata));
        Assert.Equal("videos", videos.ConfigurationKey);
    }

    [Fact]
    public void ResolveDestinations_UsesConfiguredNamesAndPrefixes()
    {
        var model = StorageModel.Build(typeof(ValidContext));

        var destinations = model.ResolveDestinations(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Photos"] = "prod-photos",
                ["videos"] = "prod-videos/hd/raw"   // prefix comes from configuration too
            },
            namePrefix: null, nameSuffix: null);

        Assert.Equal("prod-photos", destinations[typeof(PhotoMetadata)]);
        Assert.Equal("prod-videos/hd/raw", destinations[typeof(VideoMetadata)]);
    }

    [Fact]
    public void ResolveDestinations_InvalidConfiguredPrefix_Throws()
    {
        var model = StorageModel.Build(typeof(ValidContext));

        Assert.Throws<ArgumentException>(() => model.ResolveDestinations(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Photos"] = "photos/a//b",
                ["videos"] = "videos"
            },
            namePrefix: null, nameSuffix: null));
    }

    [Fact]
    public void ResolveDestinations_MissingName_Throws()
    {
        var model = StorageModel.Build(typeof(ValidContext));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            model.ResolveDestinations(new Dictionary<string, string> { ["Photos"] = "prod-photos" }, null, null));

        Assert.Contains("Objects:videos", ex.Message);
    }

    [Fact]
    public void Build_IsCached()
    {
        Assert.Same(StorageModel.Build(typeof(ValidContext)), StorageModel.Build(typeof(ValidContext)));
    }

    [Fact]
    public void Build_BucketWithoutAttribute_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => StorageModel.Build(typeof(NoAttributeContext)));
        Assert.Contains("[Bucket", ex.Message);
    }

    [Fact]
    public void Build_ReadOnlyProperty_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => StorageModel.Build(typeof(ReadOnlyPropertyContext)));
        Assert.Contains("setter", ex.Message);
    }

    [Fact]
    public void Build_SameMetadataTwice_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => StorageModel.Build(typeof(DuplicateMetadataContext)));
    }

    [Fact]
    public void ResolveDestinations_InvalidConfiguredName_Throws()
    {
        var model = StorageModel.Build(typeof(ValidContext));

        var ex = Assert.Throws<ArgumentException>(() => model.ResolveDestinations(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Photos"] = "bad bucket",
                ["videos"] = "videos"
            },
            namePrefix: null, nameSuffix: null));

        Assert.Contains("Photos", ex.Message);
    }

    [Fact]
    public void Build_IgnoresNonBucketProperties()
    {
        var model = StorageModel.Build(typeof(ValidContext));
        Assert.DoesNotContain(model.Properties, p => p.Property.Name == nameof(ValidContext.Unrelated));
    }

    [Fact]
    public void Bucket_BeforeInitialize_Throws()
    {
        var context = new ValidContext();
        Assert.Throws<InvalidOperationException>(() => context.Bucket<PhotoMetadata>());
        Assert.Throws<InvalidOperationException>(() => _ = context.Client);
    }

    public class PhotoMetadata : IObjectMetadata { }
    public class VideoMetadata : IObjectMetadata { }

    public class ValidContext : ObjectStorageContext
    {
        [Bucket]
        public IObjectBucket<PhotoMetadata> Photos { get; private set; } = null!;

        [Bucket("videos")]
        public IObjectBucket<VideoMetadata> Videos { get; init; } = null!;

        public string Unrelated { get; set; } = "x";
    }

    public class NoAttributeContext : ObjectStorageContext
    {
        public IObjectBucket<PhotoMetadata> Photos { get; private set; } = null!;
    }

    public class ReadOnlyPropertyContext : ObjectStorageContext
    {
        [Bucket("photos")]
        public IObjectBucket<PhotoMetadata> Photos => null!;
    }

    public class DuplicateMetadataContext : ObjectStorageContext
    {
        [Bucket("photos")]
        public IObjectBucket<PhotoMetadata> Photos { get; private set; } = null!;

        [Bucket("copies")]
        public IObjectBucket<PhotoMetadata> Copies { get; private set; } = null!;
    }

}
