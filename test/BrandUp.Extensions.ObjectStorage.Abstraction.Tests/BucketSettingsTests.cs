namespace BrandUp.Extensions.ObjectStorage;

public class BucketSettingsTests
{
    [Fact]
    public void Defaults_ArePrivateAndDisabled()
    {
        var settings = new BucketSettings();
        Assert.Equal(BucketAccess.Private, settings.Access);
        Assert.Equal(BucketVersioning.Disabled, settings.Versioning);
        Assert.Empty(settings.LifecycleRules);
    }

    [Fact]
    public void LifecycleRule_StoresAllFields()
    {
        var rule = new LifecycleRule("my-rule", ExpirationDays: 30, Prefix: "temp/", Enabled: false);
        Assert.Equal("my-rule", rule.Id);
        Assert.Equal(30, rule.ExpirationDays);
        Assert.Equal("temp/", rule.Prefix);
        Assert.False(rule.Enabled);
    }

    [Fact]
    public void LifecycleRule_DefaultsToEnabled()
    {
        var rule = new LifecycleRule("id");
        Assert.True(rule.Enabled);
        Assert.Null(rule.ExpirationDays);
        Assert.Null(rule.Prefix);
    }

    [Fact]
    public void LifecycleRules_CanBeAddedToSettings()
    {
        var settings = new BucketSettings();
        settings.LifecycleRules.Add(new LifecycleRule("r1", 7));
        settings.LifecycleRules.Add(new LifecycleRule("r2", 30));
        Assert.Equal(2, settings.LifecycleRules.Count);
    }

    [Fact]
    public void BucketVersioning_HasExpectedValues()
    {
        Assert.Equal(0, (int)BucketVersioning.Disabled);
        _ = BucketVersioning.Enabled;
        _ = BucketVersioning.Suspended;
    }
}
