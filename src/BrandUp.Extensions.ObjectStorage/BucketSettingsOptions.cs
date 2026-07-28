namespace BrandUp.Extensions.ObjectStorage;

/// <summary>
/// Settings applied to a bucket when it is created; existing buckets are never reconfigured. Bindable shape of
/// <see cref="BucketSettings"/>: keyed by bucket name in <see cref="ObjectStorageOptions.Buckets"/>, so
/// several context properties pointing to one bucket share a single entry.
/// </summary>
public class BucketSettingsOptions
{
    public BucketVersioning? Versioning { get; set; }
    public BucketAccess? Access { get; set; }
    public List<LifecycleRuleOptions> LifecycleRules { get; set; } = [];

    /// <summary>Applies what is set; properties left unset keep their current value.</summary>
    public void Apply(BucketSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (Versioning.HasValue)
            settings.Versioning = Versioning.Value;

        if (Access.HasValue)
            settings.Access = Access.Value;

        foreach (var rule in LifecycleRules)
            settings.LifecycleRules.Add(rule.ToRule());
    }
}

/// <summary>Bindable shape of <see cref="LifecycleRule"/>, which is a positional record.</summary>
public class LifecycleRuleOptions
{
    public string Id { get; set; } = null!;
    public int? ExpirationDays { get; set; }
    public string? Prefix { get; set; }
    public bool Enabled { get; set; } = true;

    public LifecycleRule ToRule()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id, $"{nameof(LifecycleRuleOptions)}.{nameof(Id)}");
        return new LifecycleRule(Id, ExpirationDays, Prefix, Enabled);
    }
}
