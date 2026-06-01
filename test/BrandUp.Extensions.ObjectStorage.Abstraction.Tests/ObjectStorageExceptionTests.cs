using System.Net;

namespace BrandUp.Extensions.ObjectStorage;

public class ObjectStorageExceptionTests
{
    [Fact]
    public void Constructor_SetsMessage()
    {
        var ex = new ObjectStorageException("Storage error", HttpStatusCode.InternalServerError, new Exception());
        Assert.Equal("Storage error", ex.Message);
    }

    [Fact]
    public void Constructor_SetsStatusCode()
    {
        var ex = new ObjectStorageException("Forbidden", HttpStatusCode.Forbidden, new Exception());
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }

    [Fact]
    public void Constructor_SetsInnerException()
    {
        var inner = new Exception("original");
        var ex = new ObjectStorageException("error", HttpStatusCode.BadGateway, inner);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void IsException()
    {
        var ex = new ObjectStorageException("error", HttpStatusCode.OK, new Exception());
        Assert.IsAssignableFrom<Exception>(ex);
    }
}
