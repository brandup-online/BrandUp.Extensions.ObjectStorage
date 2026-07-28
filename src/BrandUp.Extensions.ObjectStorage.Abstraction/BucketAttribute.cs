namespace BrandUp.Extensions.ObjectStorage;

/// <summary>
/// Marks an <see cref="IObjectBucket{TMetadata}"/> property of an <see cref="ObjectStorageContext"/> as a bucket.
/// Neither the bucket name nor the object key prefix are declared in code — both come from the configuration of
/// the connection the context is bound to, looked up by the key below. The property is populated when the
/// context is created.
/// </summary>
/// <param name="key">
/// Configuration key the bucket is looked up by, used instead of the property name.
/// </param>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class BucketAttribute(string? key = null) : Attribute
{
    /// <summary>Configuration key of the bucket, or <see langword="null"/> to use the property name.</summary>
    public string? Key { get; } = key;
}
