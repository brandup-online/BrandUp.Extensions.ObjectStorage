namespace BrandUp.Extensions.ObjectStorage;

/// <summary>
/// A custom object key type. Implement it on a strongly-typed identifier so it can be used as
/// <c>TKey</c> of <see cref="IObjectBucket{TMetadata, TKey}"/>; for structured keys composed of
/// properties derive from <see cref="ObjectKey"/> instead.
/// </summary>
public interface IObjectKey
{
    /// <summary>
    /// Serializes the key to the object key string. Must be deterministic: the same key value always
    /// produces the same string.
    /// </summary>
    string ToKeyString();
}
