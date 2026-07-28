namespace BrandUp.Extensions.ObjectStorage;

public class ObjectItem<TMetadata, TKey>
    where TMetadata : class, IObjectMetadata
    where TKey : notnull
{
    public TKey Id { get; init; } = default!;
    public long Size { get; init; }
    public string? ETag { get; init; }
    public TMetadata Metadata { get; init; } = null!;
}

/// <summary>Object item keyed by <see cref="Guid"/> — the historic default.</summary>
public class ObjectItem<TMetadata> : ObjectItem<TMetadata, Guid>
    where TMetadata : class, IObjectMetadata;
