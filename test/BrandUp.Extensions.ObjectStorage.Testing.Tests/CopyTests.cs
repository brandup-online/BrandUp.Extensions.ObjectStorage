using Microsoft.Extensions.DependencyInjection;

namespace BrandUp.Extensions.ObjectStorage;

/// <summary>
/// Server-side copy on the fake. The fake exists to let consumers test against the production semantics,
/// so every row of the documented behaviour is asserted here.
/// </summary>
public class CopyTests
{
    readonly FakeObjectStore _store = new();
    readonly FakeObjectBucket<FileMetadata> _files;
    readonly FakeObjectBucket<AttachmentMetadata> _attachments;

    public CopyTests()
    {
        _store.CreateBucket("files");
        _store.CreateBucket("attachments");

        _files = new FakeObjectBucket<FileMetadata>("files", "mailing", _store);
        _attachments = new FakeObjectBucket<AttachmentMetadata>("attachments", "messages", _store);
    }

    [Fact]
    public async Task CopyToAsync_BetweenBuckets_CopiesContentAndLeavesSourceInPlace()
    {
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var payload = new byte[] { 1, 2, 3, 4 };

        await _files.UploadAsync(sourceId, new FileMetadata { FileName = "video.mp4" }, new MemoryStream(payload));

        Assert.True(await _files.CopyToAsync(sourceId, _attachments, targetId,
            new AttachmentMetadata { FileName = "video.mp4", MessageId = 42 }));

        var copy = await _attachments.FindOneAsync(targetId);
        Assert.NotNull(copy);
        Assert.Equal(payload.Length, copy.Size);
        Assert.Equal(payload, await ReadAsync(_attachments, targetId));

        var source = await _files.FindOneAsync(sourceId);
        Assert.NotNull(source);
        Assert.Equal("video.mp4", source.Metadata.FileName);
    }

    [Fact]
    public async Task CopyToAsync_WithinOneBucket_CopiesUnderTheNewKey()
    {
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        await _files.UploadAsync(sourceId, new FileMetadata { FileName = "a.txt" }, new MemoryStream([7, 7]));

        Assert.True(await _files.CopyToAsync(sourceId, _files, targetId, new FileMetadata { FileName = "b.txt" }));

        var copy = await _files.FindOneAsync(targetId);
        Assert.NotNull(copy);
        Assert.Equal("b.txt", copy.Metadata.FileName);
        Assert.Equal(new byte[] { 7, 7 }, await ReadAsync(_files, targetId));

        Assert.Equal("a.txt", (await _files.FindOneAsync(sourceId))!.Metadata.FileName);
    }

    [Fact]
    public async Task CopyToAsync_ChangesMetadataType_NothingIsInheritedFromTheSource()
    {
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        // The source carries a FileName that the target metadata deliberately leaves unset.
        await _files.UploadAsync(sourceId, new FileMetadata { FileName = "invoice.pdf" }, new MemoryStream([1]));

        await _files.CopyToAsync(sourceId, _attachments, targetId,
            new AttachmentMetadata { MessageId = 7, IsInline = true });

        var copy = await _attachments.FindOneAsync(targetId);
        Assert.NotNull(copy);
        Assert.Null(copy.Metadata.FileName);            // replaced, not inherited
        Assert.Equal(7, copy.Metadata.MessageId);       // property the source type does not have at all
        Assert.True(copy.Metadata.IsInline);
    }

    [Fact]
    public async Task CopyToAsync_OntoItself_RewritesMetadataInPlace()
    {
        var id = Guid.NewGuid();
        await _files.UploadAsync(id, new FileMetadata { FileName = "old.txt" }, new MemoryStream([9]));

        Assert.True(await _files.CopyToAsync(id, _files, id, new FileMetadata { FileName = "new.txt" }));

        var item = await _files.FindOneAsync(id);
        Assert.NotNull(item);
        Assert.Equal("new.txt", item.Metadata.FileName);
        Assert.Equal(new byte[] { 9 }, await ReadAsync(_files, id));
    }

    [Fact]
    public async Task CopyToAsync_MissingSource_ReturnsFalse()
    {
        Assert.False(await _files.CopyToAsync(Guid.NewGuid(), _attachments, Guid.NewGuid(), new AttachmentMetadata()));
        Assert.Equal(0, _store.GetObjectCount("attachments"));
    }

    [Fact]
    public async Task CopyToAsync_MissingSourceBucket_ReturnsFalse()
    {
        // A HEAD against a missing bucket is a 404 all the same, so the answer is false, not NoSuchBucket.
        var missing = new FakeObjectBucket<FileMetadata>("missing", null, _store);

        Assert.False(await missing.CopyToAsync(Guid.NewGuid(), _attachments, Guid.NewGuid(), new AttachmentMetadata()));
        Assert.Equal(0, _store.GetObjectCount("attachments"));
    }

    [Fact]
    public async Task CopyToAsync_MissingTargetBucket_ThrowsNoSuchBucket()
    {
        var sourceId = Guid.NewGuid();
        await _files.UploadAsync(sourceId, new FileMetadata(), new MemoryStream([1]));

        var missing = new FakeObjectBucket<AttachmentMetadata>("missing", null, _store);

        var ex = await Assert.ThrowsAsync<ObjectStorageException>(
            () => _files.CopyToAsync(sourceId, missing, Guid.NewGuid(), new AttachmentMetadata()));
        Assert.Equal("NoSuchBucket", ex.ErrorCode);
    }

    [Fact]
    public async Task CopyToAsync_MissingSourceAndTargetBucket_ReturnsFalse()
    {
        // Production HEADs the source first, so a missing source answers before the target is ever touched.
        var missing = new FakeObjectBucket<AttachmentMetadata>("missing", null, _store);

        Assert.False(await _files.CopyToAsync(Guid.NewGuid(), missing, Guid.NewGuid(), new AttachmentMetadata()));
    }

    [Fact]
    public async Task CopyToAsync_WithOptions_AppliesThemToTheCopy()
    {
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        await _files.UploadAsync(sourceId, new FileMetadata(), new MemoryStream([1]),
            new UploadOptions { ContentType = "image/jpeg", CacheControl = "public, max-age=60" });

        await _files.CopyToAsync(sourceId, _attachments, targetId, new AttachmentMetadata(),
            new UploadOptions { ContentType = "application/octet-stream" });

        var stored = _store.GetObject("attachments", $"messages/{targetId:d}");
        Assert.NotNull(stored);
        Assert.Equal("application/octet-stream", stored.UploadOptions?.ContentType);
        Assert.Null(stored.UploadOptions?.CacheControl);   // options are taken as a whole, not merged
    }

    [Fact]
    public async Task CopyToAsync_WithoutOptions_CarriesTheSourceHeadersOver()
    {
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        await _files.UploadAsync(sourceId, new FileMetadata(), new MemoryStream([1]),
            new UploadOptions
            {
                ContentType = "image/jpeg",
                CacheControl = "public, max-age=60",
                ContentDisposition = "inline"
            });

        await _files.CopyToAsync(sourceId, _attachments, targetId, new AttachmentMetadata());

        var stored = _store.GetObject("attachments", $"messages/{targetId:d}");
        Assert.NotNull(stored);
        Assert.Equal("image/jpeg", stored.UploadOptions?.ContentType);
        Assert.Equal("public, max-age=60", stored.UploadOptions?.CacheControl);
        Assert.Equal("inline", stored.UploadOptions?.ContentDisposition);
    }

    [Fact]
    public async Task CopyToAsync_TargetFromAnotherStore_Throws()
    {
        var otherStore = new FakeObjectStore();
        otherStore.CreateBucket("attachments");
        var foreign = new FakeObjectBucket<AttachmentMetadata>("attachments", "messages", otherStore);

        var sourceId = Guid.NewGuid();
        await _files.UploadAsync(sourceId, new FileMetadata(), new MemoryStream([1]));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _files.CopyToAsync(sourceId, foreign, Guid.NewGuid(), new AttachmentMetadata()));

        Assert.Contains("same storage connection", ex.Message);
        Assert.Equal(0, otherStore.GetObjectCount("attachments"));
    }

    [Fact]
    public async Task CopyToAsync_TypedKeys_TargetKeyIsBuiltByTheTargetMapping()
    {
        _store.CreateBucket("pages");
        _store.CreateBucket("reports");

        var pages = new FakeObjectBucket<PageMetadata, string>("pages", "site", _store);
        var reports = new FakeObjectBucket<ReportMetadata, ReportKey>("reports", "archive", _store);

        await pages.UploadAsync("landing/index", new PageMetadata { Title = "Home" }, new MemoryStream([1, 2]));

        var reportKey = new ReportKey { UserId = Guid.Empty, Number = 7 };
        Assert.True(await pages.CopyToAsync("landing/index", reports, reportKey, new ReportMetadata { Title = "Snapshot" }));

        // Each bucket composes its key by its own mapping: its prefix, its key serializer.
        Assert.NotNull(_store.GetObject("pages", "site/landing/index"));
        Assert.NotNull(_store.GetObject("reports", "archive/00000000-0000-0000-0000-000000000000/7.json"));

        var copy = await reports.FindOneAsync(reportKey);
        Assert.NotNull(copy);
        Assert.Equal("Snapshot", copy.Metadata.Title);
    }

    [Fact]
    public async Task ContextFacade_CopyAsync_CopiesBetweenBucketsOfTheContext()
    {
        var services = new ServiceCollection();
        services.AddFakeObjectStorage<MailingStorage>().WithBucket("files").WithBucket("attachments");

        using var sp = services.BuildServiceProvider();
        var storage = sp.GetRequiredService<MailingStorage>();

        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        await storage.Files.UploadAsync(sourceId, new FileMetadata { FileName = "a.mp4" }, new MemoryStream([5, 5, 5]));

        Assert.True(await storage.CopyAsync<FileMetadata, AttachmentMetadata>(
            sourceId, targetId, new AttachmentMetadata { MessageId = 1 }));

        var copy = await storage.Attachments.FindOneAsync(targetId);
        Assert.NotNull(copy);
        Assert.Equal(3, copy.Size);
        Assert.Equal(1, copy.Metadata.MessageId);

        Assert.False(await storage.CopyAsync<FileMetadata, AttachmentMetadata>(
            Guid.NewGuid(), Guid.NewGuid(), new AttachmentMetadata()));
    }

    [Fact]
    public async Task ContextFacade_CopyAsync_TypedKeys()
    {
        var services = new ServiceCollection();
        services.AddFakeObjectStorage<PageStorage>().WithBucket("pages").WithBucket("reports");

        using var sp = services.BuildServiceProvider();
        var storage = sp.GetRequiredService<PageStorage>();

        await storage.Pages.UploadAsync("landing", new PageMetadata { Title = "Home" }, new MemoryStream([1]));

        var reportKey = new ReportKey { UserId = Guid.Empty, Number = 3 };
        Assert.True(await storage.CopyAsync<PageMetadata, string, ReportMetadata, ReportKey>(
            "landing", reportKey, new ReportMetadata { Title = "Snapshot" }));

        var copy = await storage.Reports.FindOneAsync(reportKey);
        Assert.NotNull(copy);
        Assert.Equal("Snapshot", copy.Metadata.Title);
    }

    [Fact]
    public async Task FakeObjectStorage_CopyAsync_CopiesBetweenMappings()
    {
        var services = new ServiceCollection();
        services.AddFakeObjectStorage()
            .AddMapping<FileMetadata>("files/mailing")
            .AddMapping<AttachmentMetadata>("attachments/messages")
            .WithBucket("files")
            .WithBucket("attachments");

        using var sp = services.BuildServiceProvider();
        var storage = sp.GetRequiredService<IObjectStorageContext>();

        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        await storage.UploadAsync(sourceId, new FileMetadata { FileName = "a.mp4" }, new MemoryStream([1, 2]));

        Assert.True(await storage.CopyAsync<FileMetadata, AttachmentMetadata>(
            sourceId, targetId, new AttachmentMetadata { MessageId = 9 }));

        var copy = await storage.FindAsync<AttachmentMetadata>(targetId);
        Assert.NotNull(copy);
        Assert.Equal(2, copy.Size);
        Assert.Equal(9, copy.Metadata.MessageId);
    }

    [Fact]
    public async Task CopyToAsync_MissingTargetBucket_FaultsTheTaskInsteadOfThrowingAtTheCallSite()
    {
        // Production reaches this condition inside an awaited S3 call, so it comes back as a faulted Task.
        // Code that starts several copies and awaits them together must catch it in the same place.
        var sourceId = Guid.NewGuid();
        await _files.UploadAsync(sourceId, new FileMetadata(), new MemoryStream([1]));

        var missing = new FakeObjectBucket<AttachmentMetadata>("missing", null, _store);

        var task = _files.CopyToAsync(sourceId, missing, Guid.NewGuid(), new AttachmentMetadata());
        await Assert.ThrowsAsync<ObjectStorageException>(() => task);
    }

    [Fact]
    public async Task CopyToAsync_HonoursCancellation()
    {
        var sourceId = Guid.NewGuid();
        await _files.UploadAsync(sourceId, new FileMetadata(), new MemoryStream([1]));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _files.CopyToAsync(
            sourceId, _attachments, Guid.NewGuid(), new AttachmentMetadata(), options: null, cts.Token));
    }

    [Fact]
    public async Task CopyToAsync_CopyIsIndependentOfTheSourceBuffer()
    {
        // A server-side copy produces two independent objects; the fake must not alias the seeded array,
        // or a test that reuses its buffer would silently mutate both.
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var content = new byte[] { 1, 2, 3 };

        _store.PutObject("files", $"mailing/{sourceId:d}", content, new FileMetadata());

        Assert.True(await _files.CopyToAsync(sourceId, _attachments, targetId, new AttachmentMetadata()));

        content[0] = 9;
        Assert.Equal([1, 2, 3], await ReadAsync(_attachments, targetId));
    }

    [Fact]
    public async Task CopyToAsync_InvalidTargetKey_ThrowsEvenWhenTheSourceIsMissing()
    {
        // Production composes the target key before it HEADs the source, so an invalid target fails the same
        // way whether or not the source exists — returning false here would hide the bug until production.
        _store.CreateBucket("pages");
        var pages = new FakeObjectBucket<PageMetadata, string>("pages", "site", _store);

        await Assert.ThrowsAsync<ArgumentNullException>(() => _files.CopyToAsync(
            Guid.NewGuid(), pages, null!, new PageMetadata()));
    }

    static async Task<byte[]> ReadAsync<TMetadata, TKey>(IObjectBucket<TMetadata, TKey> bucket, TKey id)
        where TMetadata : class, IObjectMetadata
        where TKey : notnull
    {
        await using var stream = await bucket.OpenReadAsync(id);
        Assert.NotNull(stream);

        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    public class FileMetadata : IObjectMetadata
    {
        public string? FileName { get; set; }
    }

    public class AttachmentMetadata : IObjectMetadata
    {
        public string? FileName { get; set; }
        public int MessageId { get; set; }
        public bool IsInline { get; set; }
    }

    public class PageMetadata : IObjectMetadata
    {
        public string? Title { get; set; }
    }

    public class ReportMetadata : IObjectMetadata
    {
        public string? Title { get; set; }
    }

    [ObjectKeyFormat("{UserId}/{Number}", Extension = ".json")]
    public sealed class ReportKey : ObjectKey
    {
        public Guid UserId { get; init; }
        public int Number { get; init; }
    }

    public class MailingStorage : ObjectStorageContext
    {
        [Bucket("files/mailing")]
        public IObjectBucket<FileMetadata> Files { get; private set; } = null!;

        [Bucket("attachments/messages")]
        public IObjectBucket<AttachmentMetadata> Attachments { get; private set; } = null!;
    }

    public class PageStorage : ObjectStorageContext
    {
        [Bucket("pages/site")]
        public IObjectBucket<PageMetadata, string> Pages { get; private set; } = null!;

        [Bucket("reports/archive")]
        public IObjectBucket<ReportMetadata, ReportKey> Reports { get; private set; } = null!;
    }
}
