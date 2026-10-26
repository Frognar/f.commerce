using System.Reflection;
using System.Runtime.CompilerServices;

namespace FCommerce.ArchitectureTests;

/// <summary>
/// Finds nullable types (<c>T?</c> for reference and value types, also nested in generic arguments and arrays) in the
/// signatures of declared members: fields, properties, method and constructor parameters, return types.
/// Absence is expressed with <c>Maybe&lt;T&gt;</c>; null is allowed only at the edge (see <see cref="IsEdge(Type)"/>).
/// Local variables are invisible to reflection — they stay a matter of convention.
/// </summary>
internal static class NullabilityScanner
{
    private const BindingFlags Declared =
        BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>Wire formats: HTTP binding, read by the framework.</summary>
    private static readonly string[] BindingAttributes =
        ["BindPropertyAttribute", "FromQueryAttribute", "FromRouteAttribute", "FromFormAttribute", "FromBodyAttribute", "FromHeaderAttribute"];

    private static readonly string[] EdgeTypes = ["FCommerce.SharedKernel.Optional.Nullables", "FCommerce.SharedKernel.Optional.NullableValues"];

    public static IEnumerable<string> Violations(Assembly assembly) =>
        assembly.GetTypes().Where(t => !IsEdge(t)).SelectMany(Violations);

    private static IEnumerable<string> Violations(Type type)
    {
        var context = new NullabilityInfoContext();

        foreach (var field in type.GetFields(Declared).Where(f => !IsGenerated(f)))
        {
            if (IsNullable(context.Create(field)))
            {
                yield return $"{type.FullName}.{field.Name}";
            }
        }

        foreach (var property in type.GetProperties(Declared).Where(p => !IsGenerated(p) && !IsBound(p) && !IsForeignContract(p.GetMethod ?? p.SetMethod!)))
        {
            if (IsNullable(context.Create(property)))
            {
                yield return $"{type.FullName}.{property.Name}";
            }
        }

        var methods = type.GetMethods(Declared).Where(m => !IsGenerated(m) && !IsAccessor(m) && !IsForeignContract(m));
        foreach (var method in methods)
        {
            if (IsNullable(context.Create(method.ReturnParameter)))
            {
                yield return $"{type.FullName}.{method.Name} (return)";
            }

            foreach (var parameter in method.GetParameters().Where(p => !IsPageHandler(method) && !IsBound(p) && IsNullable(context.Create(p))))
            {
                yield return $"{type.FullName}.{method.Name} ({parameter.Name})";
            }
        }

        foreach (var constructor in type.GetConstructors(Declared).Where(c => !IsGenerated(c)))
        {
            foreach (var parameter in constructor.GetParameters().Where(p => IsNullable(context.Create(p))))
            {
                yield return $"{type.FullName}..ctor ({parameter.Name})";
            }
        }
    }

    /// <summary>Contracts, EF persistence, generated code, the interop helpers and types marked as edge.</summary>
    internal static bool IsEdge(Type type) =>
        IsGenerated(type)
        || type.Assembly.GetName().Name!.EndsWith(".Contracts", StringComparison.Ordinal)
        || type.Namespace is { } contracts && contracts.EndsWith(".Contracts", StringComparison.Ordinal)
        || type.Namespace is { } ns && (ns.Contains(".Persistence", StringComparison.Ordinal)
            || ns.Contains(".Migrations", StringComparison.Ordinal)
            || ns.StartsWith("AspNetCoreGeneratedDocument", StringComparison.Ordinal)
            || ns.StartsWith("Microsoft.", StringComparison.Ordinal)
            || ns.StartsWith("System.", StringComparison.Ordinal))
        || EdgeTypes.Contains(type.FullName)
        || type.GetCustomAttributes(inherit: false).Any(a => a.GetType().Name == "NullableEdgeAttribute")
        || type.DeclaringType is { } outer && IsEdge(outer);

    private static bool IsNullable(NullabilityInfo info) =>
        info.ReadState == NullabilityState.Nullable
        || info.WriteState == NullabilityState.Nullable
        || info.ElementType is { } element && IsNullable(element)
        || info.GenericTypeArguments.Any(IsNullable);

    private static bool IsGenerated(MemberInfo member) =>
        member.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
        || member.IsDefined(typeof(System.CodeDom.Compiler.GeneratedCodeAttribute), inherit: false)
        || member.Name.Contains('<', StringComparison.Ordinal);

    private static bool IsAccessor(MethodInfo method) =>
        method.IsSpecialName && !method.Name.StartsWith("op_", StringComparison.Ordinal);

    /// <summary>Razor Pages handlers (OnGet, OnPostSaveAsync, …): their parameters are model-bound HTTP input.</summary>
    private static bool IsPageHandler(MethodInfo method) =>
        method.Name.StartsWith("On", StringComparison.Ordinal)
        && method.DeclaringType is { } type
        && Ancestors(type).Any(t => t.FullName == "Microsoft.AspNetCore.Mvc.RazorPages.PageModel");

    private static IEnumerable<Type> Ancestors(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            yield return current;
        }
    }

    /// <summary>Parameters of minimal API endpoints marked as HTTP input ([FromBody], [FromHeader], …).</summary>
    private static bool IsBound(ParameterInfo parameter) =>
        parameter.GetCustomAttributes(inherit: true).Any(a => BindingAttributes.Contains(a.GetType().Name));

    private static bool IsBound(PropertyInfo property) =>
        property.GetCustomAttributes(inherit: true).Any(a => BindingAttributes.Contains(a.GetType().Name));

    /// <summary>Overrides and implementations of members declared outside f.commerce keep their framework signature.</summary>
    private static bool IsForeignContract(MethodInfo method)
    {
        if (method.GetBaseDefinition().DeclaringType is { } declaring && declaring != method.DeclaringType && !IsOwn(declaring))
        {
            return true;
        }

        var type = method.DeclaringType!;
        return !type.IsInterface && type.GetInterfaces()
            .Where(i => !IsOwn(i))
            .Any(i => type.GetInterfaceMap(i).TargetMethods.Contains(method));
    }

    private static bool IsOwn(Type type) => type.Assembly.GetName().Name!.StartsWith("FCommerce.", StringComparison.Ordinal);
}
