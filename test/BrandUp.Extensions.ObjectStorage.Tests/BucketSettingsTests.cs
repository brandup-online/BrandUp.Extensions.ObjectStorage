using BrandUp.Extensions.ObjectStorage.Internals;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BrandUp.Extensions.ObjectStorage;

/// <summary>
/// Composition of bucket settings: what ConfigureBucket declares in code, what the connection configuration
/// says for that bucket, and how both land on physical bucket names.
/// </summary>
public class BucketSettingsTests
{
    static BucketSettings Resolve(IReadOnlyDictionary<string, Action<BucketSettings>> map, string bucketName)
    {
        var settings = new BucketSettings();
        map[bucketName].Invoke(settings);
        return settings;
    }

    static IReadOnlyDictionary<string, Action<BucketSettings>> Build(
        ObjectStorageOptions options,
        Dictionary<string, Action<BucketSettings>>? declaredInCode = null)
    {
        var model = StorageModel.Build(typeof(MediaStorage));
        var destinations = model.ResolveDestinations(options.Objects, options.BucketNamePrefix, options.BucketNameSuffix);

        return ServiceCollectionExtensions.BuildBucketSettings(
            model, destinations, options, declaredInCode ?? []);
    }

    static ObjectStorageOptions Options(string photos = "photos", string videos = "videos")
    {
        var options = new ObjectStorageOptions();
        options.Objects["Photos"] = photos;
        options.Objects["videos"] = videos;
        return options;
    }

    [Fact]
    public void NoSettings_EmptyMap()
    {
        Assert.Empty(Build(Options()));
    }

    [Fact]
    public void CodeSettings_KeyedByPhysicalName()
    {
        var map = Build(Options(photos: "prod-photos"), new()
        {
            ["Photos"] = s => s.Versioning = BucketVersioning.Enabled
        });

        Assert.Equal(BucketVersioning.Enabled, Resolve(map, "prod-photos").Versioning);
        Assert.DoesNotContain("videos", map.Keys);
    }

    [Fact]
    public void ConfigurationSettings_ByBucketName()
    {
        var options = Options(photos: "prod-photos");
        options.Buckets["prod-photos"] = new BucketSettingsOptions
        {
            Versioning = BucketVersioning.Enabled,
            LifecycleRules = [new LifecycleRuleOptions { Id = "tmp", ExpirationDays = 7, Prefix = "tmp/" }]
        };

        var settings = Resolve(Build(options), "prod-photos");

        Assert.Equal(BucketVersioning.Enabled, settings.Versioning);
        Assert.Equal(new LifecycleRule("tmp", 7, "tmp/"), Assert.Single(settings.LifecycleRules));
    }

    [Fact]
    public void Configuration_WinsOverCode()
    {
        var options = Options(photos: "prod-photos");
        options.Buckets["prod-photos"] = new BucketSettingsOptions { Access = BucketAccess.PublicRead };

        var map = Build(options, new()
        {
            ["Photos"] = s =>
            {
                s.Access = BucketAccess.Private;
                s.Versioning = BucketVersioning.Enabled;   // configuration says nothing — code value survives
            }
        });

        var settings = Resolve(map, "prod-photos");

        Assert.Equal(BucketAccess.PublicRead, settings.Access);
        Assert.Equal(BucketVersioning.Enabled, settings.Versioning);
    }

    [Fact]
    public void Configuration_KeyedByNameWithoutEnvironmentPrefix()
    {
        var options = Options(photos: "photos");
        options.BucketNamePrefix = "dev-";
        options.Buckets["photos"] = new BucketSettingsOptions { Versioning = BucketVersioning.Enabled };

        var map = Build(options);

        // Settings are declared for the bucket as written in Buckets, the map is keyed by the final name.
        Assert.Equal(BucketVersioning.Enabled, Resolve(map, "dev-photos").Versioning);
    }

    [Fact]
    public void SharedBucket_AccumulatesSettingsOfBothKeys()
    {
        var options = Options(photos: "media/photos", videos: "media/videos");
        options.Buckets["media"] = new BucketSettingsOptions
        {
            LifecycleRules = [new LifecycleRuleOptions { Id = "shared", ExpirationDays = 30 }]
        };

        var map = Build(options, new()
        {
            ["Photos"] = s => s.Versioning = BucketVersioning.Enabled,
            ["videos"] = s => s.Access = BucketAccess.PublicRead
        });

        var settings = Resolve(map, "media");

        Assert.Single(map);
        Assert.Equal(BucketVersioning.Enabled, settings.Versioning);
        Assert.Equal(BucketAccess.PublicRead, settings.Access);
        // Configuration entry of the shared bucket is applied once, not once per key.
        Assert.Equal(new LifecycleRule("shared", 30), Assert.Single(settings.LifecycleRules));
    }

    [Fact]
    public void ConfigureBucket_UnknownMetadata_Throws()
    {
        var services = new ServiceCollection();
        var builder = services.AddObjectStorage<MediaStorage>(o =>
        {
            o.ServiceUrl = "https://media.example.com";
            o.AuthenticationRegion = "us-east-1";
            o.AccessKeyId = "key";
            o.SecretAccessKey = "secret";
        });

        Assert.Throws<InvalidOperationException>(() => builder.ConfigureBucket<OtherMetadata>(_ => { }));
        var ex = Assert.Throws<InvalidOperationException>(() => builder.ConfigureBucket("unknown", _ => { }));
        Assert.Contains("Photos", ex.Message);
    }

    [Fact]
    public void BucketSettingsOptions_BindsFromConfiguration()
    {
        var section = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Media:Objects:Photos"] = "prod-photos",
            ["Media:Buckets:prod-photos:Versioning"] = "Enabled",
            ["Media:Buckets:prod-photos:Access"] = "PublicRead",
            ["Media:Buckets:prod-photos:LifecycleRules:0:Id"] = "tmp",
            ["Media:Buckets:prod-photos:LifecycleRules:0:ExpirationDays"] = "7",
            ["Media:Buckets:prod-photos:LifecycleRules:0:Prefix"] = "tmp/"
        }).Build().GetSection("Media");

        var options = new ObjectStorageOptions();
        section.Bind(options);

        var configured = options.Buckets["PROD-PHOTOS"];   // case-insensitive
        Assert.Equal(BucketVersioning.Enabled, configured.Versioning);
        Assert.Equal(BucketAccess.PublicRead, configured.Access);

        var settings = new BucketSettings();
        configured.Apply(settings);
        Assert.Equal(new LifecycleRule("tmp", 7, "tmp/"), Assert.Single(settings.LifecycleRules));
    }

    public class PhotoMetadata : IObjectMetadata { }
    public class VideoMetadata : IObjectMetadata { }
    public class OtherMetadata : IObjectMetadata { }

    public class MediaStorage : ObjectStorageContext
    {
        [Bucket]
        public IObjectBucket<PhotoMetadata> Photos { get; private set; } = null!;

        [Bucket("videos")]
        public IObjectBucket<VideoMetadata> Videos { get; private set; } = null!;
    }
}
