using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace BrandUp.Extensions.ObjectStorage;

public class ObjectStorageJsonExtensionsTests
{
    readonly IObjectStorage _storage;
    readonly IObjectBucket<ReportMetadata> _bucket;

    public ObjectStorageJsonExtensionsTests()
    {
        var services = new ServiceCollection();
        services.AddFakeObjectStorage()
            .AddMapping<ReportMetadata>("reports/items")
            .WithBucket("reports");

        var sp = services.BuildServiceProvider();
        _storage = sp.GetRequiredService<IObjectStorage>();
        _bucket  = sp.GetRequiredService<IObjectBucket<ReportMetadata>>();
    }

    // IObjectStorage tests

    [Fact]
    public async Task Storage_UploadJson_ReadJson_RoundTrip()
    {
        var id = Guid.NewGuid();
        var content = new ReportContent { Title = "Q1 Report", Value = 42.5m };

        await _storage.UploadJsonAsync<ReportMetadata, ReportContent>(
            id, new ReportMetadata { ContentType = "application/json" }, content);

        var result = await _storage.ReadJsonAsync<ReportMetadata, ReportContent>(id);

        Assert.NotNull(result);
        Assert.Equal("Q1 Report", result.Title);
        Assert.Equal(42.5m, result.Value);
    }

    [Fact]
    public async Task Storage_ReadJson_ReturnsNull_WhenNotFound()
    {
        var result = await _storage.ReadJsonAsync<ReportMetadata, ReportContent>(Guid.NewGuid());
        Assert.Null(result);
    }

    [Fact]
    public async Task Storage_UploadJson_ReturnsItemWithCorrectMetadata()
    {
        var id = Guid.NewGuid();
        var metadata = new ReportMetadata { ContentType = "application/json" };

        var item = await _storage.UploadJsonAsync<ReportMetadata, ReportContent>(
            id, metadata, new ReportContent { Title = "test" });

        Assert.Equal(id, item.Id);
        Assert.Equal("application/json", item.Metadata.ContentType);
        Assert.True(item.Size > 0);
    }

    [Fact]
    public async Task Storage_UploadJson_WithOptions_UsesOptions()
    {
        var id = Guid.NewGuid();
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        await _storage.UploadJsonAsync<ReportMetadata, ReportContent>(
            id, new ReportMetadata(), new ReportContent { Title = "camel" }, options);

        // Verify raw bytes contain camelCase key
        await using var stream = await _storage.ReadAsync<ReportMetadata>(id);
        using var reader = new StreamReader(stream!);
        var json = await reader.ReadToEndAsync();
        Assert.Contains("\"title\"", json);
        Assert.DoesNotContain("\"Title\"", json);
    }

    [Fact]
    public async Task Storage_ReadJson_WithOptions_DeserializesCorrectly()
    {
        var id = Guid.NewGuid();
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        await _storage.UploadJsonAsync<ReportMetadata, ReportContent>(
            id, new ReportMetadata(), new ReportContent { Title = "deserialize test" }, options);

        var result = await _storage.ReadJsonAsync<ReportMetadata, ReportContent>(id, options);
        Assert.NotNull(result);
        Assert.Equal("deserialize test", result.Title);
    }

    // IObjectBucket<TMetadata> tests

    [Fact]
    public async Task Bucket_UploadJson_ReadJson_RoundTrip()
    {
        var id = Guid.NewGuid();
        var content = new ReportContent { Title = "Bucket Report", Value = 99.9m };

        await _bucket.UploadJsonAsync<ReportMetadata, ReportContent>(
            id, new ReportMetadata(), content);

        var result = await _bucket.ReadJsonAsync<ReportMetadata, ReportContent>(id);

        Assert.NotNull(result);
        Assert.Equal("Bucket Report", result.Title);
        Assert.Equal(99.9m, result.Value);
    }

    [Fact]
    public async Task Bucket_ReadJson_ReturnsNull_WhenNotFound()
    {
        var result = await _bucket.ReadJsonAsync<ReportMetadata, ReportContent>(Guid.NewGuid());
        Assert.Null(result);
    }

    [Fact]
    public async Task Storage_UploadJson_ThrowsForNullMetadata()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _storage.UploadJsonAsync<ReportMetadata, ReportContent>(
                Guid.NewGuid(), null!, new ReportContent()));
    }

    [Fact]
    public async Task Storage_UploadJson_ThrowsForNullContent()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _storage.UploadJsonAsync<ReportMetadata, ReportContent>(
                Guid.NewGuid(), new ReportMetadata(), null!));
    }

    // Test types

    private class ReportMetadata : IObjectMetadata
    {
        public string ContentType { get; set; } = "application/json";
    }

    private class ReportContent
    {
        public string Title { get; set; } = "";
        public decimal Value { get; set; }
    }
}
