namespace BrandUp.Extensions.ObjectStorage;

public class ObjectKeyTests
{
    [Fact]
    public void DefaultLayout_JoinsPropertiesWithSlash()
    {
        var key = new PlainKey { UserId = Guid.Parse("11111111-2222-3333-4444-555555555555"), Number = 42 };

        Assert.Equal("11111111-2222-3333-4444-555555555555/42", key.ToKeyString());
    }

    [Fact]
    public void Template_FormatsValuesInvariantly()
    {
        var key = new ReportKey
        {
            UserId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            CreatedOn = new DateOnly(2026, 7, 28),
            Number = 7
        };

        Assert.Equal("reports/11111111-2222-3333-4444-555555555555/2026/07/7.json", key.ToKeyString());
    }

    [Fact]
    public void Extension_LeadingDotIsOptional()
    {
        Assert.EndsWith(".jpg", new NoDotExtensionKey { Name = "photo" }.ToKeyString());
    }

    [Fact]
    public void NoExtension_NothingAppended()
    {
        // No Extension declared -> no extension and no dot in the key.
        Assert.Equal("photo", new PlainStringKey { Name = "photo" }.ToKeyString());
    }

    [Theory]
    [InlineData(typeof(DotOnlyExtensionKey))]
    [InlineData(typeof(InvalidCharExtensionKey))]
    public void InvalidExtension_Throws(Type keyType)
    {
        var key = (ObjectKey)Activator.CreateInstance(keyType)!;
        var ex = Assert.Throws<InvalidOperationException>(key.ToKeyString);
        Assert.Contains(keyType.Name, ex.Message);
    }

    [Fact]
    public void NullProperty_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new PlainStringKey().ToKeyString());
        Assert.Contains(nameof(PlainStringKey.Name), ex.Message);
    }

    [Fact]
    public void EmptyPropertyValue_Throws()
    {
        // "" would produce an invisible segment (e.g. a key consisting only of the extension).
        var ex = Assert.Throws<InvalidOperationException>(() => new NoDotExtensionKey { Name = "" }.ToKeyString());
        Assert.Contains(nameof(NoDotExtensionKey.Name), ex.Message);
    }

    [Fact]
    public void InheritedKey_BasePropertiesComeFirst()
    {
        var key = new DerivedKey { Tenant = "acme", Number = 7 };

        Assert.Equal("acme/7", key.ToKeyString());
    }

    [Fact]
    public void Template_UnknownProperty_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new BrokenTemplateKey { Name = "x" }.ToKeyString());
        Assert.Contains("Missing", ex.Message);
    }

    [Fact]
    public void Key_WithAwsAvoidCharacter_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => new PlainStringKey { Name = "report#7" }.ToKeyString());
        Assert.Contains("characters to avoid", ex.Message);
    }

    [Fact]
    public void KeyWithoutProperties_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new ConstantKey().ToKeyString());
    }

    [Fact]
    public void Equality_ByTypeAndKeyString()
    {
        var a = new PlainKey { UserId = Guid.Empty, Number = 1 };
        var b = new PlainKey { UserId = Guid.Empty, Number = 1 };
        var c = new PlainKey { UserId = Guid.Empty, Number = 2 };

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal(a.ToKeyString(), a.ToString());
    }

    sealed class PlainKey : ObjectKey
    {
        public Guid UserId { get; init; }
        public int Number { get; init; }
    }

    [ObjectKeyFormat("reports/{UserId}/{CreatedOn:yyyy/MM}/{Number}", Extension = ".json")]
    sealed class ReportKey : ObjectKey
    {
        public Guid UserId { get; init; }
        public DateOnly CreatedOn { get; init; }
        public int Number { get; init; }
    }

    [ObjectKeyFormat(Extension = "jpg")]
    sealed class NoDotExtensionKey : ObjectKey
    {
        public string Name { get; init; } = null!;
    }

    sealed class PlainStringKey : ObjectKey
    {
        public string? Name { get; init; }
    }

    [ObjectKeyFormat("{Missing}")]
    sealed class BrokenTemplateKey : ObjectKey
    {
        public string Name { get; init; } = null!;
    }

    [ObjectKeyFormat("constant", Extension = ".txt")]
    sealed class ConstantKey : ObjectKey;

    abstract class TenantKey : ObjectKey
    {
        public string Tenant { get; init; } = null!;
    }

    sealed class DerivedKey : TenantKey
    {
        public int Number { get; init; }
    }

    [ObjectKeyFormat(Extension = ".")]
    sealed class DotOnlyExtensionKey : ObjectKey
    {
        public string Name { get; init; } = "x";
    }

    [ObjectKeyFormat(Extension = "jp g")]
    sealed class InvalidCharExtensionKey : ObjectKey
    {
        public string Name { get; init; } = "x";
    }
}
