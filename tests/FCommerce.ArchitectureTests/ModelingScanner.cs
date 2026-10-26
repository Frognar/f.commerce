using System.Reflection;
using System.Runtime.CompilerServices;

namespace FCommerce.ArchitectureTests;

/// <summary>
/// Finds domain types that can be built in an invalid state: a public constructor (or a public setter) that takes text
/// or a number, which only a <c>Parse</c>/<c>Validate</c> function may turn into a domain value, and public
/// <c>init</c> setters on types whose constructor is not public (a <c>with</c> expression would bypass it).
/// Raw input types (<c>[RawInput]</c>) and wire formats (<c>*.Contracts</c> namespaces) are exempt; they are parsed
/// before anything trusts them.
/// </summary>
internal static class ModelingScanner
{
    /// <summary>Text and numbers: not every value of these is a valid value of a domain concept.</summary>
    private static readonly Type[] Partial = [typeof(string), typeof(decimal), typeof(int), typeof(long), typeof(short), typeof(double), typeof(float)];

    /// <summary>The functional core whose types model the domain (Search.Core is an algorithm over text, not a model).</summary>
    public static IEnumerable<Assembly> Scope =>
        Solution.All.Where(a => a.Name() is "FCommerce.SharedKernel" or "FCommerce.Authorization.Core" || a.Name().EndsWith(".Domain", StringComparison.Ordinal));

    /// <summary>Violations per type (full name → what is wrong).</summary>
    public static IEnumerable<(string Type, string Detail)> Violations(Assembly assembly) =>
        assembly.GetTypes().Where(IsModel).SelectMany(type => Violations(type).Select(detail => (type.FullName!, detail)));

    private static IEnumerable<string> Violations(Type type)
    {
        var constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public).Where(c => !IsCopyConstructor(c)).ToList();
        foreach (var constructor in constructors)
        {
            foreach (var parameter in constructor.GetParameters().Where(p => IsPartial(p.ParameterType)))
            {
                yield return $".ctor ({parameter.Name}: {Name(parameter.ParameterType)})";
            }
        }

        var parameters = constructors.SelectMany(c => c.GetParameters()).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
        {
            if (property.SetMethod is not { IsPublic: true } setter)
            {
                continue;
            }

            if (constructors.Count == 0 && IsInit(setter))
            {
                yield return $"{property.Name} (public init behind a non-public constructor)";
            }
            else if (IsPartial(property.PropertyType) && !parameters.Contains(property.Name))
            {
                yield return $"{property.Name} (public setter: {Name(property.PropertyType)})";
            }
        }
    }

    private static bool IsModel(Type type) =>
        type.IsVisible
        && !type.IsInterface
        && !type.IsEnum
        && !typeof(Attribute).IsAssignableFrom(type)
        && !typeof(Delegate).IsAssignableFrom(type)
        && !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
        && !(type.Namespace is { } ns && ns.EndsWith(".Contracts", StringComparison.Ordinal))
        && !IsRawInput(type);

    private static bool IsRawInput(Type type) =>
        type.GetCustomAttributes(inherit: false).Any(a => a.GetType().Name == "RawInputAttribute")
        || type.DeclaringType is { } outer && IsRawInput(outer);

    /// <summary>Text or a number, also inside arrays, generic arguments (<c>Maybe&lt;string&gt;</c>) and tuples.</summary>
    internal static bool IsPartial(Type type) =>
        Partial.Contains(type)
        || type.IsArray && IsPartial(type.GetElementType()!)
        || type.IsGenericType && type.GetGenericArguments().Any(IsPartial);

    private static bool IsCopyConstructor(ConstructorInfo constructor) =>
        constructor.GetParameters() is [var only] && only.ParameterType == constructor.DeclaringType;

    private static bool IsInit(MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit));

    private static string Name(Type type) =>
        type.IsGenericType ? $"{type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)]}<{string.Join(", ", type.GetGenericArguments().Select(Name))}>" : type.Name;
}
