using System.ComponentModel;
using System.Globalization;

namespace BrandUp.Extensions.ObjectStorage.Internals;

/// <summary>
/// Invariant string conversion of metadata property values. The single codec shared by the S3 mapping
/// layer and the Testing fake, so schema-evolution reads (a value written by one metadata type and read
/// through another) behave identically against both.
/// </summary>
internal static class MetadataValueConverter
{
    static readonly string[] DateTimeFormats = ["yyyy-MM-dd", "o"];

    public static string ToInvariantString(object value)
    {
        if (value is string str)
            return str;

        if (value is DateTime date)
        {
            if (date.Kind == DateTimeKind.Local)
                date = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
            return date.TimeOfDay == TimeSpan.Zero
                ? date.ToString("yyyy-MM-dd")
                : date.ToString("o", CultureInfo.InvariantCulture);
        }

        var converter = TypeDescriptor.GetConverter(value.GetType());
        return converter.ConvertToInvariantString(value) ?? string.Empty;
    }

    /// <param name="str">Invariant string produced by <see cref="ToInvariantString"/>.</param>
    /// <param name="targetType">Non-nullable target type (unwrap <see cref="Nullable{T}"/> before calling).</param>
    public static object FromInvariantString(string str, Type targetType)
    {
        if (targetType == typeof(string))
            return str;
        if (targetType == typeof(DateTime))
            return DateTime.ParseExact(str, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        var converter = TypeDescriptor.GetConverter(targetType);
        return converter.ConvertFromInvariantString(str)
            ?? throw new InvalidOperationException($"Cannot convert '{str}' to {targetType.Name}.");
    }
}
