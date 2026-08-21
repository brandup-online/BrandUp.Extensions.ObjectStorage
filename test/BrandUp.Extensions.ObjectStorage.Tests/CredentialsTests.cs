using Amazon.Runtime;
using BrandUp.Extensions.ObjectStorage.Internals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BrandUp.Extensions.ObjectStorage;

public class CredentialsTests
{
    static ObjectStorageOptions BaseOptions() => new()
    {
        ServiceUrl = "http://localhost:9000",
        AuthenticationRegion = "us-east-1"
    };

    #region CreateCredentials

    [Fact]
    public void CreateCredentials_StaticKeys_UsesBasicWithoutToken()
    {
        var opts = BaseOptions();
        opts.AccessKeyId = "ak";
        opts.SecretAccessKey = "sk";

        var creds = S3Client.CreateCredentials(opts, provider: null);

        Assert.IsType<BasicAWSCredentials>(creds);
        var immutable = creds.GetCredentials();
        Assert.False(immutable.UseToken);
        Assert.True(string.IsNullOrEmpty(immutable.Token));
    }

    [Fact]
    public void CreateCredentials_SessionToken_CarriesSecurityToken()
    {
        var opts = BaseOptions();
        opts.AccessKeyId = "ak";
        opts.SecretAccessKey = "sk";
        opts.SessionToken = "session-token-123";

        var creds = S3Client.CreateCredentials(opts, provider: null);

        Assert.IsType<SessionAWSCredentials>(creds);
        var immutable = creds.GetCredentials();
        Assert.True(immutable.UseToken);
        Assert.Equal("session-token-123", immutable.Token);
    }

    [Fact]
    public void CreateCredentials_Provider_TakesPriorityOverStaticAndSession()
    {
        var opts = BaseOptions();
        opts.AccessKeyId = "static-ak";
        opts.SecretAccessKey = "static-sk";
        opts.SessionToken = "static-session";

        var provider = new FakeCredentialsProvider(new("p-ak", "p-sk", "p-token", DateTimeOffset.UtcNow.AddHours(2)));
        var creds = S3Client.CreateCredentials(opts, provider);

        var immutable = creds.GetCredentials();
        Assert.Equal("p-ak", immutable.AccessKey);
        Assert.Equal("p-token", immutable.Token);
    }

    [Fact]
    public void CreateCredentials_Provider_RefreshesInPlace_WithoutRecreatingClient()
    {
        var provider = new FakeCredentialsProvider(new("ak1", "sk1", "tok1", DateTimeOffset.UtcNow.AddHours(2)));
        var creds = (RefreshingAWSCredentials)S3Client.CreateCredentials(BaseOptions(), provider);

        Assert.Equal("tok1", creds.GetCredentials().Token);

        // Consumer renews its cache out-of-band; the same credentials object must pick up the new values.
        provider.Current = new("ak2", "sk2", "tok2", DateTimeOffset.UtcNow.AddHours(2));
        creds.ClearCredentials(); // simulate expiry / forced refresh

        var refreshed = creds.GetCredentials();
        Assert.Equal("ak2", refreshed.AccessKey);
        Assert.Equal("tok2", refreshed.Token);
    }

    [Fact]
    public void CreateCredentials_Provider_NullExpiry_StillProducesCredentials()
    {
        var provider = new FakeCredentialsProvider(new("ak", "sk", "tok", ExpiresUtc: null));
        var creds = S3Client.CreateCredentials(BaseOptions(), provider);

        var immutable = creds.GetCredentials();
        Assert.Equal("ak", immutable.AccessKey);
        Assert.Equal("tok", immutable.Token);
    }

    #endregion

    #region Validator

    [Fact]
    public void Validator_StaticKeys_Succeeds()
    {
        var opts = BaseOptions();
        opts.AccessKeyId = "ak";
        opts.SecretAccessKey = "sk";

        var result = new ObjectStorageOptionsValidator().Validate(null, opts);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validator_NoKeysNoProvider_Fails()
    {
        var result = new ObjectStorageOptionsValidator().Validate(null, BaseOptions());
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validator_ProviderRegistered_KeysNotRequired()
    {
        var result = new ObjectStorageOptionsValidator(new CredentialsProviderMarker()).Validate(null, BaseOptions());
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65)]    // above the guard ceiling: the setting multiplies the buffer memory of every upload
    public void Validator_MultipartParallelismOutOfRange_Fails(int parallelism)
    {
        var opts = BaseOptions();
        opts.AccessKeyId = "ak";
        opts.SecretAccessKey = "sk";
        opts.MultipartParallelism = parallelism;

        var result = new ObjectStorageOptionsValidator().Validate(null, opts);
        Assert.True(result.Failed);
        Assert.Contains(nameof(ObjectStorageOptions.MultipartParallelism), result.FailureMessage);
    }

    [Theory]
    [InlineData(null, "us-east-1")]
    [InlineData("http://localhost:9000", null)]
    public void Validator_MissingServiceUrlOrRegion_FailsEvenWithProvider(string? serviceUrl, string? region)
    {
        var opts = new ObjectStorageOptions { ServiceUrl = serviceUrl, AuthenticationRegion = region };
        var result = new ObjectStorageOptionsValidator(new CredentialsProviderMarker()).Validate(null, opts);
        Assert.True(result.Failed);
    }

    #endregion

    #region DI wiring

    [Fact]
    public void ProviderMode_NoStaticKeys_OptionsValidationPasses_AndClientResolves()
    {
        var services = new ServiceCollection();
        var builder = services.AddObjectStorage(o =>
        {
            o.ServiceUrl = "http://localhost:9000";
            o.AuthenticationRegion = "us-east-1";
        });
        builder.UseCredentialsProvider(_ =>
            new FakeCredentialsProvider(new("ak", "sk", "tok", DateTimeOffset.UtcNow.AddHours(2))));

        using var sp = services.BuildServiceProvider();

        // Accessing .Value runs IValidateOptions; must not throw despite missing ak/sk.
        var validated = sp.GetRequiredService<IOptions<ObjectStorageOptions>>().Value;
        Assert.NotNull(validated);

        // Building the S3 client (which constructs the AmazonS3Client) must succeed with provider creds.
        Assert.NotNull(sp.GetRequiredService<IObjectStorageClient>());
    }

    [Fact]
    public void ProviderMode_GenericOverload_RegistersProvider_AndClientResolves()
    {
        var services = new ServiceCollection();
        var builder = services.AddObjectStorage(o =>
        {
            o.ServiceUrl = "http://localhost:9000";
            o.AuthenticationRegion = "us-east-1";
        });
        builder.UseCredentialsProvider<StubCredentialsProvider>();

        using var sp = services.BuildServiceProvider();

        Assert.IsType<StubCredentialsProvider>(sp.GetRequiredService<IObjectStorageCredentialsProvider>());
        // Validation passes without static keys, and the S3 client builds with provider credentials.
        Assert.NotNull(sp.GetRequiredService<IOptions<ObjectStorageOptions>>().Value);
        Assert.NotNull(sp.GetRequiredService<IObjectStorageClient>());
    }

    [Fact]
    public void StaticMode_NoKeys_OptionsValidationThrows()
    {
        var services = new ServiceCollection();
        services.AddObjectStorage(o =>
        {
            o.ServiceUrl = "http://localhost:9000";
            o.AuthenticationRegion = "us-east-1";
        });

        using var sp = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => _ = sp.GetRequiredService<IOptions<ObjectStorageOptions>>().Value);
    }

    #endregion

    sealed class FakeCredentialsProvider(ObjectStorageCredentials current) : IObjectStorageCredentialsProvider
    {
        public ObjectStorageCredentials Current { get; set; } = current;
        public ObjectStorageCredentials GetCurrent() => Current;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    // Parameterless provider so DI can activate it via the generic UseCredentialsProvider<TProvider>() overload.
    sealed class StubCredentialsProvider : IObjectStorageCredentialsProvider
    {
        public ObjectStorageCredentials GetCurrent() => new("ak", "sk", "tok", DateTimeOffset.UtcNow.AddHours(2));
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
