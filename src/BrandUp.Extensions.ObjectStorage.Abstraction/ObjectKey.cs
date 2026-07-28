using BrandUp.Extensions.ObjectStorage.Internals;

namespace BrandUp.Extensions.ObjectStorage;

/// <summary>
/// Base class for structured object keys: the key string is composed from the public properties of the
/// derived type, with an optional file extension appended automatically.
/// </summary>
/// <remarks>
/// By default the properties are joined with <c>/</c> in declaration order. <see cref="ObjectKeyFormatAttribute"/>
/// overrides the layout with a template (<c>{Property}</c> or <c>{Property:format}</c> placeholders) and declares
/// the extension. Formatting is invariant; <see cref="Guid"/> uses the <c>d</c> format by default.
/// Key types are identifiers and must be immutable (use <c>init</c> accessors): the key string is computed
/// once per instance and cached.
/// </remarks>
/// <example>
/// <code>
/// [ObjectKeyFormat("{UserId}/{CreatedOn:yyyy/MM}/{Number}", Extension = ".json")]
/// public sealed class ReportKey : ObjectKey
/// {
///     public Guid UserId { get; init; }
///     public DateOnly CreatedOn { get; init; }
///     public int Number { get; init; }
/// }
/// // new ReportKey { UserId = ..., CreatedOn = new(2026, 7, 28), Number = 7 }
/// //   -> "1f0f.../2026/07/7.json"
/// </code>
/// </example>
public abstract class ObjectKey : IObjectKey
{
    string? _keyString;

    /// <inheritdoc/>
    public string ToKeyString() => _keyString ??= ObjectKeyModel.Get(GetType()).Format(this);

    /// <summary>
    /// Two keys are equal when they are of the same type and produce the same key string. Note that comparing
    /// (or hashing) an incomplete key — one whose properties would make <see cref="ToKeyString"/> throw —
    /// throws as well.
    /// </summary>
    public override bool Equals(object? obj)
        => obj is ObjectKey other && other.GetType() == GetType() && other.ToKeyString() == ToKeyString();

    public override int GetHashCode() => HashCode.Combine(GetType(), ToKeyString());

    public sealed override string ToString() => ToKeyString();
}

/// <summary>
/// Declares how an <see cref="ObjectKey"/>-derived type is rendered: an optional template with
/// <c>{Property}</c> / <c>{Property:format}</c> placeholders (default — properties joined with <c>/</c>
/// in declaration order) and an optional file extension appended to the result.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class ObjectKeyFormatAttribute(string? format = null) : Attribute
{
    /// <summary>Key template, e.g. <c>{UserId}/{CreatedOn:yyyy/MM}/{Number}</c>; <see langword="null"/> for the default layout.</summary>
    public string? Format { get; } = format;

    /// <summary>File extension appended to the key, e.g. <c>.json</c>; the leading dot is optional.</summary>
    public string? Extension { get; set; }
}
