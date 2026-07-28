using System.Linq.Expressions;
using System.Reflection;

namespace BrandUp.Extensions.ObjectStorage.Internals;

/// <summary>Compiled property access shared by the mapping and key-formatting models.</summary>
internal static class PropertyGetters
{
    public static Func<object, object?> Compile(PropertyInfo property)
    {
        var instance = Expression.Parameter(typeof(object), "obj");
        return Expression.Lambda<Func<object, object?>>(
            Expression.Convert(
                Expression.Property(Expression.Convert(instance, property.DeclaringType!), property),
                typeof(object)),
            instance).Compile();
    }
}
