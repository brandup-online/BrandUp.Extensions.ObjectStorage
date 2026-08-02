namespace BrandUp.Extensions.ObjectStorage;

/// <summary>
/// HTTP-facing attributes of an uploaded object. Unlike metadata, these are served back by the storage as
/// response headers — a browser needs the right <see cref="ContentType"/> to display a file instead of
/// downloading it.
/// </summary>
public class UploadOptions
{
    /// <summary>MIME type served as <c>Content-Type</c>, e.g. <c>image/jpeg</c>.</summary>
    public string? ContentType { get; set; }

    /// <summary>Caching policy served as <c>Cache-Control</c>, e.g. <c>public, max-age=31536000, immutable</c>.</summary>
    public string? CacheControl { get; set; }

    /// <summary>Presentation hint served as <c>Content-Disposition</c>, e.g. <c>attachment; filename="report.pdf"</c>.</summary>
    public string? ContentDisposition { get; set; }
}
