namespace FCommerce.ArchitectureTests;

/// <summary>
/// Dependency rules at assembly level. Purity *inside* the core (no clock, IO, randomness) is enforced at
/// compile time by BannedApiAnalyzers — see build/BannedSymbols.Pure.txt.
/// </summary>
public sealed class DependencyRulesTests
{
    private static readonly string[] DatabaseLibraries = ["Marten", "JasperFx", "Weasel", "Npgsql"];

    [Fact]
    public void Solution_assemblies_are_discovered()
    {
        Assert.Contains(Solution.All, a => a.Name() == "FCommerce.Host");
        Assert.Contains(Solution.Pure, a => a.Name() == "FCommerce.SharedKernel");
    }

    [Fact]
    public void Functional_core_depends_only_on_the_base_library_and_the_shared_kernel()
    {
        var violations = Solution.Pure
            .SelectMany(a => a.References()
                .Where(r => !IsBaseLibrary(r) && r != "FCommerce.SharedKernel")
                .Select(r => $"{a.Name()} → {r}"))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void Only_the_database_adapter_knows_the_database_libraries()
    {
        var violations = Solution.All
            .Where(a => !a.Name().StartsWith("FCommerce.Infrastructure.", StringComparison.Ordinal))
            .SelectMany(a => a.References()
                .Where(r => DatabaseLibraries.Any(lib => r.StartsWith(lib, StringComparison.Ordinal)))
                .Select(r => $"{a.Name()} → {r}"))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void Application_layer_does_not_depend_on_infrastructure_or_web()
    {
        var application = Solution.All.Single(a => a.Name() == "FCommerce.Application");

        Assert.DoesNotContain(application.References(), r =>
            r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || r.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || r.StartsWith("FCommerce.Infrastructure", StringComparison.Ordinal));
    }

    [Fact]
    public void Modules_reach_other_modules_only_through_their_contracts()
    {
        var violations = Solution.All
            .Select(a => (Assembly: a.Name(), Module: Solution.ModuleOf(a.Name())))
            .Where(x => x.Module is not null)
            .SelectMany(x => Solution.All.Single(a => a.Name() == x.Assembly).References()
                .Where(r => Solution.ModuleOf(r) is { } other && other != x.Module && !Solution.IsContracts(r))
                .Select(r => $"{x.Assembly} → {r}"))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void Contracts_depend_only_on_the_shared_kernel()
    {
        var violations = Solution.All
            .Where(a => Solution.IsContracts(a.Name()))
            .SelectMany(a => a.References()
                .Where(r => !IsBaseLibrary(r) && r != "FCommerce.SharedKernel")
                .Select(r => $"{a.Name()} → {r}"))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void Host_uses_modules_only_through_contracts_and_registration()
    {
        var host = Solution.All.Single(a => a.Name() == "FCommerce.Host");

        var violations = host.References()
            .Where(r => Solution.ModuleOf(r) is not null && r.EndsWith(".Domain", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(violations);
    }

    private static bool IsBaseLibrary(string name) =>
        name is "System.Runtime" or "netstandard" or "mscorlib"
        || name.StartsWith("System.", StringComparison.Ordinal);
}
