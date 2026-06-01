namespace BrandUp.Extensions.ObjectStorage;

public class ObjectItem<TMetadata> where TMetadata : class, IObjectMetadata
{
    public Guid Id { get; init; }
    public long Size { get; init; }
    public string? ETag { get; init; }
    public TMetadata Metadata { get; init; } = null!;
}
