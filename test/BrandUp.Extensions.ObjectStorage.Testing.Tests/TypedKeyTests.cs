using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace BrandUp.Extensions.ObjectStorage;

/// <summary>Typed object keys, end to end through a fake storage context.</summary>
public class TypedKeyTests
{
    static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddFakeObjectStorage<DocumentStorage>()
            .WithBucket("reports").WithBucket("pages").WithBucket("invoices").WithBucket("photos").WithBucket("scans");

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task StringKey_RoundTrip()
    {
        using var sp = Build();
        var storage = sp.GetRequiredService<DocumentStorage>();

        await storage.Pages.UploadAsync("landing/index", new PageMetadata { Title = "Home" },
            new MemoryStream(Encoding.UTF8.GetBytes("<html/>")));

        var found = await storage.Pages.FindOneAsync("landing/index");
        Assert.NotNull(found);
        Assert.Equal("landing/index", found.Id);
        Assert.Equal("Home", found.Metadata.Title);

        Assert.True(await storage.Pages.DeleteOneAsync("landing/index"));
        Assert.Null(await storage.Pages.FindOneAsync("landing/index"));
    }

    [Fact]
    public async Task LongKey_RoundTrip()
    {
        using var sp = Build();
        var storage = sp.GetRequiredService<DocumentStorage>();

        await storage.Invoices.UploadAsync(20260728001, new InvoiceMetadata(), new MemoryStream([1]));

        var found = await storage.Invoices.FindOneAsync(20260728001);
        Assert.NotNull(found);
        Assert.Equal(20260728001, found.Id);
    }

    [Fact]
    public async Task ObjectKey_RoundTrip_AndKeyShape()
    {
        using var sp = Build();
        var storage = sp.GetRequiredService<DocumentStorage>();
        var key = new ReportKey { UserId = Guid.Empty, Number = 7 };

        await storage.Reports.UploadJsonAsync(key, new ReportMetadata(), new { value = 42 });

        var found = await storage.Reports.FindOneAsync(new ReportKey { UserId = Guid.Empty, Number = 7 });
        Assert.NotNull(found);

        // The stored object key is the serialized structured key (with extension), under the declared prefix.
        Assert.Equal("00000000-0000-0000-0000-000000000000/7.json", key.ToKeyString());
    }

    [Fact]
    public async Task GuidBucket_KeepsHistoricShape()
    {
        using var sp = Build();
        var storage = sp.GetRequiredService<DocumentStorage>();
        var id = Guid.NewGuid();

        // Item type is the historic ObjectItem<TMetadata>; the bucket is polymorphic as IObjectBucket<TM, Guid>.
        ObjectItem<PhotoMetadata> item = await storage.Photos.UploadAsync(id, new PhotoMetadata(), new MemoryStream([1]));
        Assert.Equal(id, item.Id);

        IObjectBucket<PhotoMetadata, Guid> generic = storage.Photos;
        var found = await generic.FindOneAsync(id);
        Assert.NotNull(found);
        Assert.Equal(id, found.Id);
    }

    [Fact]
    public void TypedBuckets_ResolvableFromDi()
    {
        using var sp = Build();
        var storage = sp.GetRequiredService<DocumentStorage>();

        Assert.Same(storage.Pages, sp.GetRequiredService<IObjectBucket<PageMetadata, string>>());
        Assert.Same(storage.Photos, sp.GetRequiredService<IObjectBucket<PhotoMetadata>>());
    }

    [Fact]
    public void ContextFacade_TypedKeyAccessors()
    {
        using var sp = Build();
        var storage = sp.GetRequiredService<DocumentStorage>();

        Assert.Same(storage.Pages, storage.Bucket<PageMetadata, string>());
        var ex = Assert.Throws<InvalidOperationException>(() => storage.Bucket<PageMetadata>());
        Assert.Contains("not keyed by Guid", ex.Message);
    }

    [Theory]
    [InlineData("pages\\index")]
    [InlineData("pages{1}")]
    [InlineData("100%")]
    [InlineData("a#b")]
    public async Task StringKey_WithAwsAvoidCharacter_ThrowsBeforeAnyRequest(string key)
    {
        using var sp = Build();
        var storage = sp.GetRequiredService<DocumentStorage>();

        // Validation is client-side: the exception is thrown while composing the object key,
        // before any storage call is made.
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => storage.Pages.UploadAsync(key, new PageMetadata(), new MemoryStream([1])));
        Assert.Contains("characters to avoid", ex.Message);

        await Assert.ThrowsAsync<ArgumentException>(() => storage.Pages.FindOneAsync(key));
    }

    [Fact]
    public async Task ExplicitGuidKeyedProperty_GetsRichHistoricShape()
    {
        using var sp = Build();
        var storage = sp.GetRequiredService<DocumentStorage>();
        var id = Guid.NewGuid();

        // A property declared as IObjectBucket<TM, Guid> receives the same rich implementation as
        // IObjectBucket<TM>: both facade accessors work, DI resolves the exact property type, and items
        // come back as the historic ObjectItem<TM>.
        Assert.Same(storage.Scans, storage.Bucket<ScanMetadata>());
        Assert.Same(storage.Scans, storage.Bucket<ScanMetadata, Guid>());
        Assert.Same(storage.Scans, sp.GetRequiredService<IObjectBucket<ScanMetadata, Guid>>());

        var item = await storage.Scans.UploadAsync(id, new ScanMetadata(), new MemoryStream([1]));
        Assert.IsType<ObjectItem<ScanMetadata>>(item);
        Assert.Equal(id, item.Id);
    }

    [Fact]
    public void UnsupportedKeyType_FailsAtRegistration()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddFakeObjectStorage<BrokenStorage>());
        Assert.Contains(nameof(IObjectKey), ex.Message);
    }

    public class ReportMetadata : IObjectMetadata { }
    public class PageMetadata : IObjectMetadata { public string? Title { get; set; } }
    public class InvoiceMetadata : IObjectMetadata { }
    public class PhotoMetadata : IObjectMetadata { }
    public class ScanMetadata : IObjectMetadata { }

    [ObjectKeyFormat("{UserId}/{Number}", Extension = ".json")]
    public sealed class ReportKey : ObjectKey
    {
        public Guid UserId { get; init; }
        public int Number { get; init; }
    }

    public class DocumentStorage : ObjectStorageContext
    {
        [Bucket("reports")]
        public IObjectBucket<ReportMetadata, ReportKey> Reports { get; private set; } = null!;

        [Bucket("pages")]
        public IObjectBucket<PageMetadata, string> Pages { get; private set; } = null!;

        [Bucket("invoices")]
        public IObjectBucket<InvoiceMetadata, long> Invoices { get; private set; } = null!;

        [Bucket("photos")]
        public IObjectBucket<PhotoMetadata> Photos { get; private set; } = null!;

        [Bucket("scans")]
        public IObjectBucket<ScanMetadata, Guid> Scans { get; private set; } = null!;
    }

    public class BrokenStorage : ObjectStorageContext
    {
        [Bucket("data")]
        public IObjectBucket<ReportMetadata, byte[]> Data { get; private set; } = null!;
    }
}
