namespace BrandUp.Extensions.ObjectStorage.Internals;

/// <summary>Shared upload-stream checks, so the fake rejects exactly what production rejects.</summary>
internal static class UploadStreamGuard
{
    /// <summary>
    /// A non-empty seekable stream positioned at its end is almost always a forgotten rewind; uploading
    /// it would silently overwrite the object with an empty body. Genuinely empty streams (markers,
    /// placeholders) are legal in S3 and pass through.
    /// </summary>
    public static void ThrowIfConsumed(Stream stream)
    {
        if (stream.CanSeek && stream.Length > 0 && stream.Position == stream.Length)
            throw new InvalidOperationException(
                "Stream is positioned at its end. Rewind it before uploading, or pass an empty stream to store an empty object.");
    }
}
