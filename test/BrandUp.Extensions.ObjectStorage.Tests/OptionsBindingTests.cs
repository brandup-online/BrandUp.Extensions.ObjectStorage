using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BrandUp.Extensions.ObjectStorage;

/// <summary>
/// Bucket names are meant to be taken from configuration, so binding <see cref="ObjectStorageOptions"/> to a
/// configuration section must actually populate <see cref="ObjectStorageOptions.Objects"/> — the property is
/// get-only, and the binder has to fill the existing (case-insensitive) dictionary.
/// </summary>
public class OptionsBindingTests
{
    static IConfiguration Configuration(params (string Key, string Value)[] entries)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void Bind_PopulatesBuckets()
    {
        var section = Configuration(
            ("Media:ServiceUrl", "https://media.example.com"),
            ("Media:AuthenticationRegion", "ru-central1"),
            ("Media:AccessKeyId", "key"),
            ("Media:SecretAccessKey", "secret"),
            ("Media:BucketNameSuffix", "-dev"),
            ("Media:Objects:Photos", "prod-photos"),
            ("Media:Objects:videos", "prod-videos/hd")).GetSection("Media");

        var options = new ObjectStorageOptions();
        section.Bind(options);

        Assert.Equal("https://media.example.com", options.ServiceUrl);
        Assert.Equal("-dev", options.BucketNameSuffix);
        Assert.Equal(2, options.Objects.Count);
        Assert.Equal("prod-photos", options.Objects["Photos"]);
        Assert.Equal("prod-videos/hd", options.Objects["videos"]);
    }

    [Fact]
    public void Bind_KeepsCaseInsensitiveLookup()
    {
        var section = Configuration(("Media:Objects:PHOTOS", "prod-photos")).GetSection("Media");

        var options = new ObjectStorageOptions();
        section.Bind(options);

        Assert.True(options.Objects.ContainsKey("photos"));
        Assert.Equal("prod-photos", options.Objects["Photos"]);
    }

    [Fact]
    public void BoundOptions_DriveContextBuckets()
    {
        var section = Configuration(
            ("Media:ServiceUrl", "https://media.example.com"),
            ("Media:AuthenticationRegion", "ru-central1"),
            ("Media:AccessKeyId", "key"),
            ("Media:SecretAccessKey", "secret"),
            ("Media:Objects:photos", "prod-photos"),
            ("Media:Objects:Videos", "prod-videos/hd")).GetSection("Media");

        var services = new ServiceCollection();
        services.AddObjectStorage<BoundStorage>(o => section.Bind(o));

        using var sp = services.BuildServiceProvider();
        var storage = sp.GetRequiredService<BoundStorage>();

        Assert.Equal("prod-photos", storage.Photos.Name);
        Assert.Equal("prod-videos", storage.Videos.Name);
    }

    public class PhotoMetadata : IObjectMetadata { }
    public class VideoMetadata : IObjectMetadata { }

    public class BoundStorage : ObjectStorageContext
    {
        [Bucket]
        public IObjectBucket<PhotoMetadata> Photos { get; private set; } = null!;

        [Bucket("videos")]
        public IObjectBucket<VideoMetadata> Videos { get; private set; } = null!;
    }
}
