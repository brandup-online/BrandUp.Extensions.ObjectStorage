using System.Net;

namespace BrandUp.Extensions.ObjectStorage;

public class ObjectStorageException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public ObjectStorageException(string message, HttpStatusCode statusCode, Exception inner)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}
