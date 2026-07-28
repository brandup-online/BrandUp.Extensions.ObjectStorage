namespace BrandUp.Extensions.ObjectStorage.Internals;

/// <summary>
/// Which owner (storage context or the default connection) serves which metadata type. Shared by the real
/// registration pipeline and the Testing package, so the ambiguity contract stays identical in both.
/// </summary>
internal sealed class MetadataOwners
{
    readonly Dictionary<Type, Type> _owners = [];

    /// <summary>
    /// Claims <paramref name="metadataType"/> for <paramref name="owner"/>. Returns <see langword="false"/> when
    /// another owner already claimed it — a bare <c>IObjectBucket&lt;TMetadata&gt;</c> registration would then
    /// be ambiguous.
    /// </summary>
    public bool Claim(Type metadataType, Type owner, out Type currentOwner)
    {
        if (_owners.TryGetValue(metadataType, out var existing))
        {
            currentOwner = existing;
            return existing == owner;
        }

        _owners[metadataType] = owner;
        currentOwner = owner;

        return true;
    }

    /// <summary>The single wording of the ambiguous-bucket error, whoever reports it.</summary>
    public static string AmbiguityMessage(Type metadataType, string currentOwnerDisplay, string newOwnerDisplay)
        => $"Metadata type {metadataType.FullName} is mapped both in {currentOwnerDisplay} and in {newOwnerDisplay}. " +
           $"Inject the storage context instead of IObjectBucket<{metadataType.Name}>.";
}
