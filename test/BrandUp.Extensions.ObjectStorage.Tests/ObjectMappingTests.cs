using BrandUp.Extensions.ObjectStorage.Internals;

namespace BrandUp.Extensions.ObjectStorage;

public class ObjectMappingTests
{
    [Theory]
    [InlineData("mybucket", "mybucket", null)]
    [InlineData("mybucket/prefix", "mybucket", "prefix")]
    [InlineData("mybucket/sub/prefix", "mybucket", "sub/prefix")]
    [InlineData("MyBucket/Prefix", "mybucket", "Prefix")]   // bucket lowered, prefix case kept
    public void Create_ParsesDestination(string destination, string expectedBucket, string? expectedPrefix)
    {
        var mapping = ObjectMapping.Create(typeof(PersonMetadata), destination);
        Assert.Equal(expectedBucket, mapping.BucketName);
        Assert.Equal(expectedPrefix, mapping.ObjectKeyPrefix);
    }

    [Fact]
    public void Create_ThrowsForMissingConstructor()
    {
        Assert.Throws<ArgumentException>(() =>
            ObjectMapping.Create(typeof(NoConstructorMetadata), "bucket"));
    }

    [Fact]
    public void GetObjectKey_WithoutPrefix_ReturnsIdAsIs()
    {
        var mapping = ObjectMapping.Create(typeof(PersonMetadata), "bucket");
        var id = Guid.NewGuid().ToString("d");
        Assert.Equal(id, mapping.GetObjectKey(id));
    }

    [Fact]
    public void GetObjectKey_WithPrefix_JoinsWithSlash()
    {
        var mapping = ObjectMapping.Create(typeof(PersonMetadata), "bucket/items");
        var id = Guid.NewGuid().ToString("d");
        Assert.Equal($"items/{id}", mapping.GetObjectKey(id));
    }

    [Fact]
    public void Create_KeepsPrefixCase_LowersBucketOnly()
    {
        var mapping = ObjectMapping.Create(typeof(PersonMetadata), "MyBucket/Photos/2026");
        Assert.Equal("mybucket", mapping.BucketName);
        Assert.Equal($"Photos/2026/x", mapping.GetObjectKey("x"));
    }

    [Fact]
    public void Deserialize_MissingKeys_LeaveDefaults()
    {
        // Schema evolution: an object stored before Age/IsActive existed must stay readable.
        var mapping = ObjectMapping.Create(typeof(PersonMetadata), "bucket");
        var data = new Dictionary<string, string> { [nameof(PersonMetadata.Name)] = "Alice" };

        var restored = (PersonMetadata)mapping.Deserialize(data);

        Assert.Equal("Alice", restored.Name);
        Assert.Equal(0, restored.Age);
        Assert.False(restored.IsActive);
        Assert.Null(restored.BirthDate);
    }

    [Fact]
    public void Deserialize_NullValue_LeavesDefault_EvenForValueType()
    {
        var mapping = ObjectMapping.Create(typeof(PersonMetadata), "bucket");
        var data = new Dictionary<string, string>
        {
            [nameof(PersonMetadata.Name)] = null!,
            [nameof(PersonMetadata.Age)] = null!
        };

        var restored = (PersonMetadata)mapping.Deserialize(data);

        Assert.Null(restored.Name);
        Assert.Equal(0, restored.Age);
    }

    [Fact]
    public void MetadataKeys_MatchPropertyNames()
    {
        var mapping = ObjectMapping.Create(typeof(PersonMetadata), "bucket");
        var keys = mapping.MetadataKeys.ToHashSet();
        Assert.Contains(nameof(PersonMetadata.Name), keys);
        Assert.Contains(nameof(PersonMetadata.Age), keys);
        Assert.Contains(nameof(PersonMetadata.IsActive), keys);
    }

    [Fact]
    public void Serialize_SkipsNullValues()
    {
        var mapping = ObjectMapping.Create(typeof(PersonMetadata), "bucket");
        var metadata = new PersonMetadata { Name = null, Age = 25 };
        var data = mapping.Serialize(metadata);
        Assert.False(data.ContainsKey(nameof(PersonMetadata.Name)));
        Assert.True(data.ContainsKey(nameof(PersonMetadata.Age)));
    }

    [Fact]
    public void Deserialize_RestoresValues()
    {
        var mapping = ObjectMapping.Create(typeof(PersonMetadata), "bucket");
        var original = new PersonMetadata { Name = "Alice", Age = 30, IsActive = true };
        var data = mapping.Serialize(original);
        var restored = (PersonMetadata)mapping.Deserialize(data);
        Assert.Equal("Alice", restored.Name);
        Assert.Equal(30, restored.Age);
        Assert.True(restored.IsActive);
    }

    [Fact]
    public void Serialize_Deserialize_DateTime_RoundTrip()
    {
        var mapping = ObjectMapping.Create(typeof(PersonMetadata), "bucket");
        var date = new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var original = new PersonMetadata { BirthDate = date };
        var data = mapping.Serialize(original);
        var restored = (PersonMetadata)mapping.Deserialize(data);
        Assert.Equal(date.Date, restored.BirthDate?.Date);
    }

    [Fact]
    public void Serialize_NullableProperty_WhenNull_IsSkipped()
    {
        var mapping = ObjectMapping.Create(typeof(PersonMetadata), "bucket");
        var original = new PersonMetadata { BirthDate = null };
        var data = mapping.Serialize(original);
        Assert.False(data.ContainsKey(nameof(PersonMetadata.BirthDate)));
    }

    private class PersonMetadata : IObjectMetadata
    {
        public string? Name { get; set; }
        public int Age { get; set; }
        public bool IsActive { get; set; }
        public DateTime? BirthDate { get; set; }
    }

    private class NoConstructorMetadata : IObjectMetadata
    {
        public string Value { get; set; }
        public NoConstructorMetadata(string value) { Value = value; }
    }
}
