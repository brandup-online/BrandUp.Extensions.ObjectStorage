using BrandUp.Extensions.ObjectStorage.Internals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BrandUp.Extensions.ObjectStorage;

public class StorageContextTests
{
    static Action<ObjectStorageOptions> Options(string serviceUrl, string region = "us-east-1") => o =>
    {
        o.ServiceUrl = serviceUrl;
        o.AuthenticationRegion = region;
        o.AccessKeyId = "key";
        o.SecretAccessKey = "secret";
    };

    /// <summary>Options of a connection serving <see cref="MediaStorage"/>: bucket names live only here.</summary>
    static Action<ObjectStorageOptions> MediaOptions(string serviceUrl = "https://media.example.com", string region = "us-east-1") => o =>
    {
        Options(serviceUrl, region)(o);
        o.Objects["Photos"] = "photos";      // by property name
        o.Objects["videos"] = "videos/hd";   // by key from [Bucket("videos")], with an object key prefix
    };

    static Action<ObjectStorageOptions> ArchiveOptions(string serviceUrl = "https://archive.example.com", string region = "us-east-1") => o =>
    {
        Options(serviceUrl, region)(o);
        o.Objects["archive"] = "cold-archive/arch";
    };

    #region Registration

    [Fact]
    public void AddObjectStorage_Context_ResolvesWithPopulatedBuckets()
    {
        var services = new ServiceCollection();
        services.AddObjectStorage<MediaStorage>(MediaOptions());

        using var sp = services.BuildServiceProvider();
        var storage = sp.GetRequiredService<MediaStorage>();

        Assert.NotNull(storage.Photos);
        Assert.NotNull(storage.Videos);
        Assert.Equal("photos", storage.Photos.Name);
        Assert.Equal("videos", storage.Videos.Name);
        Assert.NotNull(storage.Client);
    }

    [Fact]
    public void AddObjectStorage_Context_RegistersTypedBuckets()
    {
        var services = new ServiceCollection();
        services.AddObjectStorage<MediaStorage>(MediaOptions());

        using var sp = services.BuildServiceProvider();

        Assert.Same(sp.GetRequiredService<MediaStorage>().Photos, sp.GetRequiredService<IObjectBucket<PhotoMetadata>>());
    }

    [Fact]
    public void Context_ExposesBucketsByMetadataType()
    {
        var services = new ServiceCollection();
        services.AddObjectStorage<MediaStorage>(MediaOptions());

        using var sp = services.BuildServiceProvider();
        var storage = sp.GetRequiredService<MediaStorage>();

        Assert.IsAssignableFrom<IObjectStorageContext>(storage);
        Assert.Same(storage.Photos, storage.Bucket<PhotoMetadata>());
        Assert.Equal(2, storage.Buckets.Count);
        Assert.Throws<InvalidOperationException>(() => storage.Bucket<ArchiveMetadata>());
    }

    [Fact]
    public void AddObjectStorage_SameContextTwice_Throws()
    {
        var services = new ServiceCollection();
        services.AddObjectStorage<MediaStorage>(MediaOptions());

        Assert.Throws<InvalidOperationException>(() =>
            services.AddObjectStorage<MediaStorage>(MediaOptions("https://other.example.com")));
    }

    [Fact]
    public void AddObjectStorage_UnknownConnection_ThrowsOnResolve()
    {
        var services = new ServiceCollection();
        services.AddObjectStorage<MediaStorage>("missing");

        using var sp = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<MediaStorage>());
        Assert.Contains("missing", ex.Message);
    }

    #endregion

    #region Bucket names come from configuration

    [Fact]
    public void BucketName_LookedUpByPropertyName()
    {
        var services = new ServiceCollection();
        services.AddObjectStorage<MediaStorage>(o =>
        {
            MediaOptions()(o);
            o.Objects["Photos"] = "prod-photos";
        });

        using var sp = services.BuildServiceProvider();

        Assert.Equal("prod-photos", sp.GetRequiredService<MediaStorage>().Photos.Name);
    }

    [Fact]
    public void BucketName_LookedUpByKeyFromAttribute()
    {
        var services = new ServiceCollection();
        services.AddObjectStorage<MediaStorage>(o =>
        {
            MediaOptions()(o);
            o.Objects["videos"] = "prod-videos";
        });

        using var sp = services.BuildServiceProvider();

        Assert.Equal("prod-videos", sp.GetRequiredService<MediaStorage>().Videos.Name);
    }

    [Fact]
    public void ConfigurationKeys_AreCaseInsensitive()
    {
        var services = new ServiceCollection();
        services.AddObjectStorage<MediaStorage>(o =>
        {
            Options("https://media.example.com")(o);
            o.Objects["photos"] = "prod-photos";   // property is Photos
            o.Objects["VIDEOS"] = "prod-videos";   // key from the attribute is videos
        });

        using var sp = services.BuildServiceProvider();
        var storage = sp.GetRequiredService<MediaStorage>();

        Assert.Equal("prod-photos", storage.Photos.Name);
        Assert.Equal("prod-videos", storage.Videos.Name);
    }

    [Fact]
    public void BucketName_NotConfigured_ThrowsOnResolve()
    {
        var services = new ServiceCollection();
        services.AddObjectStorage<MediaStorage>(o =>
        {
            Options("https://media.example.com")(o);
            o.Objects["Photos"] = "photos";   // videos is missing
        });

        using var sp = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<MediaStorage>());
        Assert.Contains("MediaStorage.Videos", ex.Message);
        Assert.Contains("Objects:videos", ex.Message);
    }

    [Fact]
    public void ConfiguredValue_CarriesObjectKeyPrefix()
    {
        var services = new ServiceCollection();
        services.AddObjectStorage<MediaStorage>(o =>
        {
            MediaOptions()(o);
            o.Objects["videos"] = "shared/videos/sd";
        });

        using var sp = services.BuildServiceProvider();

        Assert.Equal("shared", sp.GetRequiredService<MediaStorage>().Videos.Name);
    }

    [Fact]
    public void ConfiguredPrefix_SeparatesKeysInOneBucket()
    {
        var services = new ServiceCollection();
        services.AddObjectStorage<MediaStorage>(o =>
        {
            MediaOptions()(o);
            o.Objects["Photos"] = "media/photos";
            o.Objects["videos"] = "media/videos";
        });

        using var sp = services.BuildServiceProvider();
        var storage = sp.GetRequiredService<MediaStorage>();

        Assert.Equal("media", storage.Photos.Name);
        Assert.Equal("media", storage.Videos.Name);
    }

    [Fact]
    public void BucketNamePrefixAndSuffix_WrapConfiguredNames()
    {
        var services = new ServiceCollection();
        services.AddObjectStorage<MediaStorage>(o =>
        {
            MediaOptions()(o);
            o.BucketNamePrefix = "acme-";
            o.BucketNameSuffix = "-dev";
        });

        using var sp = services.BuildServiceProvider();
        var storage = sp.GetRequiredService<MediaStorage>();

        Assert.Equal("acme-photos-dev", storage.Photos.Name);
        Assert.Equal("acme-videos-dev", storage.Videos.Name);
    }

    [Fact]
    public void InvalidConfiguredBucketName_ThrowsOnResolve()
    {
        var services = new ServiceCollection();
        services.AddObjectStorage<MediaStorage>(o =>
        {
            MediaOptions()(o);
            o.Objects["Photos"] = "bad bucket";
        });

        using var sp = services.BuildServiceProvider();

        var ex = Assert.Throws<ArgumentException>(() => sp.GetRequiredService<MediaStorage>());
        Assert.Contains("Photos", ex.Message);
    }

    [Fact]
    public void InvalidBucketNameSuffix_ThrowsOnResolve()
    {
        var services = new ServiceCollection();
        services.AddObjectStorage<MediaStorage>(o =>
        {
            MediaOptions()(o);
            o.BucketNameSuffix = "-";   // bucket name cannot end with '-'
        });

        using var sp = services.BuildServiceProvider();

        Assert.Throws<ArgumentException>(() => sp.GetRequiredService<MediaStorage>());
    }

    [Fact]
    public void DefaultConnection_MappingNameIsOverridableAndOptional()
    {
        var services = new ServiceCollection();
        services.AddObjectStorage(o =>
        {
            Options("https://legacy.example.com")(o);
            o.Objects["legacy"] = "prod-legacy";
            o.BucketNameSuffix = "-1";
        })
        .AddMapping<LegacyMetadata>("legacy/items")
        .AddMapping<OtherLegacyMetadata>("other");

        using var sp = services.BuildServiceProvider();

        // AddMapping declares the name in code, so configuration is optional there.
        Assert.Equal("prod-legacy-1", sp.GetRequiredService<IObjectBucket<LegacyMetadata>>().Name);
        Assert.Equal("other-1", sp.GetRequiredService<IObjectBucket<OtherLegacyMetadata>>().Name);
    }

    #endregion

    #region Connections

    [Fact]
    public void EachContext_HasOwnAccountOptions()
    {
        var services = new ServiceCollection();
        services.AddObjectStorage<MediaStorage>(MediaOptions("https://media.example.com", "ru-central1"));
        services.AddObjectStorage<ArchiveStorage>(ArchiveOptions("https://archive.example.com", "eu-central-1"));

        using var sp = services.BuildServiceProvider();
        var monitor = sp.GetRequiredService<IOptionsMonitor<ObjectStorageOptions>>();

        Assert.Equal("https://media.example.com", monitor.Get(typeof(MediaStorage).FullName).ServiceUrl);
        Assert.Equal("ru-central1", monitor.Get(typeof(MediaStorage).FullName).AuthenticationRegion);
        Assert.Equal("https://archive.example.com", monitor.Get(typeof(ArchiveStorage).FullName).ServiceUrl);
        Assert.Equal("eu-central-1", monitor.Get(typeof(ArchiveStorage).FullName).AuthenticationRegion);
        Assert.Equal("cold-archive", sp.GetRequiredService<ArchiveStorage>().Items.Name);
    }

    [Fact]
    public void SeparateContexts_UseSeparateS3Clients()
    {
        var services = new ServiceCollection();
        services.AddObjectStorage<MediaStorage>(MediaOptions());
        services.AddObjectStorage<ArchiveStorage>(ArchiveOptions());

        using var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IS3ClientFactory>();

        Assert.NotSame(
            factory.Get(typeof(MediaStorage).FullName!),
            factory.Get(typeof(ArchiveStorage).FullName!));
    }

    [Fact]
    public void SharedConnection_ContextsShareOneS3Client()
    {
        var services = new ServiceCollection();
        services.AddObjectStorageConnection("main", o =>
        {
            MediaOptions("https://main.example.com")(o);
            o.Objects["Reports"] = "reports";
        });
        services.AddObjectStorage<MediaStorage>("main");
        services.AddObjectStorage<ReportStorage>("main");

        using var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<ObjectStorageRegistry>();
        var factory = sp.GetRequiredService<IS3ClientFactory>();

        Assert.Equal("main", registry.Contexts[typeof(MediaStorage)]);
        Assert.Equal("main", registry.Contexts[typeof(ReportStorage)]);
        Assert.Same(factory.Get("main"), factory.Get("main"));

        // Both contexts resolve, each with its own bucket set over the shared connection.
        Assert.Equal("photos", sp.GetRequiredService<MediaStorage>().Photos.Name);
        Assert.Equal("reports", sp.GetRequiredService<ReportStorage>().Reports.Name);
    }

    [Fact]
    public void ConnectionRegisteredAfterContext_StillResolves()
    {
        var services = new ServiceCollection();
        services.AddObjectStorage<MediaStorage>("main");
        services.AddObjectStorageConnection("main", MediaOptions("https://main.example.com"));

        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<MediaStorage>());
    }

    [Fact]
    public void DuplicateMetadataAcrossContexts_ContextsWork_BareBucketThrows()
    {
        var services = new ServiceCollection();
        services.AddObjectStorage<MediaStorage>(MediaOptions());
        services.AddObjectStorage<MirrorStorage>(o =>
        {
            Options("https://mirror.example.com")(o);
            o.Objects["Photos"] = "mirror";
        });

        using var sp = services.BuildServiceProvider();

        Assert.Equal("photos", sp.GetRequiredService<MediaStorage>().Photos.Name);
        Assert.Equal("mirror", sp.GetRequiredService<MirrorStorage>().Photos.Name);

        var ex = Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<IObjectBucket<PhotoMetadata>>());
        Assert.Contains(nameof(MirrorStorage), ex.Message);
    }

    #endregion

    #region Credentials

    [Fact]
    public void UseCredentialsProvider_IsPerConnection()
    {
        var services = new ServiceCollection();

        // No static keys: valid only because a provider is registered for this connection.
        services.AddObjectStorage<MediaStorage>(o =>
        {
            o.ServiceUrl = "https://media.example.com";
            o.AuthenticationRegion = "us-east-1";
            o.Objects["Photos"] = "photos";
            o.Objects["videos"] = "videos";
        }).UseCredentialsProvider<StubCredentialsProvider>();

        services.AddObjectStorage<ArchiveStorage>(ArchiveOptions());

        using var sp = services.BuildServiceProvider();
        var monitor = sp.GetRequiredService<IOptionsMonitor<ObjectStorageOptions>>();

        Assert.NotNull(monitor.Get(typeof(MediaStorage).FullName));
        Assert.NotNull(monitor.Get(typeof(ArchiveStorage).FullName));

        Assert.IsType<StubCredentialsProvider>(
            sp.GetKeyedService<IObjectStorageCredentialsProvider>(typeof(MediaStorage).FullName));
        Assert.Null(sp.GetKeyedService<IObjectStorageCredentialsProvider>(typeof(ArchiveStorage).FullName));

        Assert.NotNull(sp.GetRequiredService<MediaStorage>());
    }

    [Fact]
    public void ValidateOnStart_False_DefersValidationToFirstUse()
    {
        var services = new ServiceCollection();

        // Deliberately invalid options (no ServiceUrl): a worker whose storage is optional.
        services.AddObjectStorage<MediaStorage>(o => { }, validateOnStart: false);

        using var sp = services.BuildServiceProvider();

        // Host start does not touch the options: either no startup validator was registered at all,
        // or it has nothing to validate for this connection.
        var startupValidator = sp.GetService<IStartupValidator>();
        if (startupValidator is not null)
            startupValidator.Validate();

        // Validation still guards actual use.
        Assert.Throws<OptionsValidationException>(() =>
            sp.GetRequiredService<IOptionsMonitor<ObjectStorageOptions>>().Get(typeof(MediaStorage).FullName));
    }

    [Fact]
    public void ValidateOnStart_Default_FailsEagerly()
    {
        var services = new ServiceCollection();
        services.AddObjectStorage<MediaStorage>(o => { });

        using var sp = services.BuildServiceProvider();

        var startupValidator = sp.GetRequiredService<IStartupValidator>();
        Assert.Throws<OptionsValidationException>(startupValidator.Validate);
    }

    [Fact]
    public void NoCredentials_ValidationFailsForThatConnectionOnly()
    {
        var services = new ServiceCollection();
        services.AddObjectStorage<MediaStorage>(o =>
        {
            o.ServiceUrl = "https://media.example.com";
            o.AuthenticationRegion = "us-east-1";
        });
        services.AddObjectStorage<ArchiveStorage>(ArchiveOptions());

        using var sp = services.BuildServiceProvider();
        var monitor = sp.GetRequiredService<IOptionsMonitor<ObjectStorageOptions>>();

        var ex = Assert.Throws<OptionsValidationException>(() => monitor.Get(typeof(MediaStorage).FullName));
        Assert.Contains(typeof(MediaStorage).FullName!, ex.Message);
        Assert.NotNull(monitor.Get(typeof(ArchiveStorage).FullName));
    }

    #endregion

    #region Coexistence with the default connection

    [Fact]
    public void DefaultConnection_AndContexts_Coexist()
    {
        var services = new ServiceCollection();
        services.AddObjectStorage(Options("https://legacy.example.com"))
            .AddMapping<LegacyMetadata>("legacy/items");
        services.AddObjectStorage<MediaStorage>(MediaOptions());

        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<IObjectStorageClient>());
        Assert.NotNull(sp.GetRequiredService<IObjectStorageContext>());
        Assert.Equal("legacy", sp.GetRequiredService<IObjectBucket<LegacyMetadata>>().Name);
        Assert.Equal("photos", sp.GetRequiredService<MediaStorage>().Photos.Name);

        var monitor = sp.GetRequiredService<IOptionsMonitor<ObjectStorageOptions>>();
        Assert.Equal("https://legacy.example.com", monitor.CurrentValue.ServiceUrl);
        Assert.Equal("https://media.example.com", monitor.Get(typeof(MediaStorage).FullName).ServiceUrl);
    }

    #endregion

    public class PhotoMetadata : IObjectMetadata { }
    public class VideoMetadata : IObjectMetadata { }
    public class ArchiveMetadata : IObjectMetadata { }
    public class ReportMetadata : IObjectMetadata { }
    public class LegacyMetadata : IObjectMetadata { }
    public class OtherLegacyMetadata : IObjectMetadata { }

    public class MediaStorage : ObjectStorageContext
    {
        [Bucket]                        // configuration key: Photos
        public IObjectBucket<PhotoMetadata> Photos { get; private set; } = null!;

        [Bucket("videos")]              // configuration key: videos
        public IObjectBucket<VideoMetadata> Videos { get; private set; } = null!;
    }

    public class ArchiveStorage : ObjectStorageContext
    {
        [Bucket("archive")]
        public IObjectBucket<ArchiveMetadata> Items { get; private set; } = null!;
    }

    public class ReportStorage : ObjectStorageContext
    {
        [Bucket]
        public IObjectBucket<ReportMetadata> Reports { get; private set; } = null!;
    }

    public class MirrorStorage : ObjectStorageContext
    {
        [Bucket]
        public IObjectBucket<PhotoMetadata> Photos { get; private set; } = null!;
    }

    sealed class StubCredentialsProvider : IObjectStorageCredentialsProvider
    {
        public ObjectStorageCredentials GetCurrent() => new("ak", "sk", "tok", DateTimeOffset.UtcNow.AddHours(2));
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
