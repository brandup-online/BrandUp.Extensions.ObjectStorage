namespace BrandUp.Extensions.ObjectStorage.Internals;

/// <summary>
/// Marker registered alongside an <see cref="IObjectStorageCredentialsProvider"/> so the options validator
/// can detect provider mode without constructing the (potentially heavy) provider during startup validation.
/// </summary>
internal sealed class CredentialsProviderMarker;
