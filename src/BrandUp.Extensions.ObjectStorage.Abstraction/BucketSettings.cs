namespace BrandUp.Extensions.ObjectStorage;

public class BucketSettings
{
    public BucketVersioning Versioning { get; set; } = BucketVersioning.Disabled;
    public BucketAccess Access { get; set; } = BucketAccess.Private;
    public List<LifecycleRule> LifecycleRules { get; set; } = [];
}

public enum BucketVersioning { Disabled, Enabled, Suspended }

public enum BucketAccess { Private, PublicRead }

public record LifecycleRule(
    string Id,
    int? ExpirationDays = null,
    string? Prefix = null,
    bool Enabled = true);
