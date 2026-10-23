using System.Reflection;

namespace FCommerce.ArchitectureTests;

/// <summary>All f.commerce assemblies deployed with the host, classified by role.</summary>
internal static class Solution
{
    public static readonly IReadOnlyList<Assembly> All =
    [
        .. Directory.GetFiles(AppContext.BaseDirectory, "FCommerce.*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !name!.EndsWith("Tests", StringComparison.Ordinal))
            .Select(name => Assembly.Load(name!))
            .OrderBy(a => a.GetName().Name),
    ];

    public static readonly string[] BuildingBlocks =
    [
        "FCommerce.SharedKernel",
        "FCommerce.Application",
        "FCommerce.Infrastructure",
        "FCommerce.Infrastructure.Postgres",
        "FCommerce.Host",
    ];

    public static string Name(this Assembly assembly) => assembly.GetName().Name!;

    public static IEnumerable<string> References(this Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(r => r.Name!);

    /// <summary>Functional core: SharedKernel and every module's *.Domain.</summary>
    public static IEnumerable<Assembly> Pure =>
        All.Where(a => a.Name() == "FCommerce.SharedKernel" || a.Name().EndsWith(".Domain", StringComparison.Ordinal));

    /// <summary>Module name ("Catalog") for module assemblies, null for building blocks.</summary>
    public static string? ModuleOf(string assemblyName)
    {
        if (!assemblyName.StartsWith("FCommerce.", StringComparison.Ordinal) || BuildingBlocks.Contains(assemblyName))
        {
            return null;
        }

        return assemblyName["FCommerce.".Length..].Split('.')[0];
    }

    public static bool IsContracts(string assemblyName) => assemblyName.EndsWith(".Contracts", StringComparison.Ordinal);
}
