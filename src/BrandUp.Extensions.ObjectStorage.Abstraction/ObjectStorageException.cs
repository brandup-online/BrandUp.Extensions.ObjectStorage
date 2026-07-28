using System.Net;

namespace BrandUp.Extensions.ObjectStorage;

public class ObjectStorageException : Exception
{
    public HttpStatusCode StatusCode { get; }

    /// <summary>Provider error code, e.g. <c>BucketAlreadyOwnedByYou</c>; <see langword="null"/> if unknown.</summary>
    public string? ErrorCode { get; }

    public ObjectStorageException(string message, HttpStatusCode statusCode, Exception inner)
        : this(message, statusCode, null, inner)
    {
    }

    public ObjectStorageException(string message, HttpStatusCode statusCode, string? errorCode, Exception inner)
        : base(message, inner)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    /// <summary>
    /// The bucket already exists — either owned by this account or taken by another one. Providers report it as
    /// <c>BucketAlreadyOwnedByYou</c> / <c>BucketAlreadyExists</c> with HTTP 409.
    /// </summary>
    public bool IsBucketAlreadyExists
        => StatusCode == HttpStatusCode.Conflict
            || ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists";
}
