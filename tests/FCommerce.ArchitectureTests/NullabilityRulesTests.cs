namespace FCommerce.ArchitectureTests;

/// <summary>
/// No null in signatures: absence is <c>Maybe&lt;T&gt;</c>, which (like Result) exposes its value only through Match.
/// Nullable primitives stay at the edge only — contracts, EF rows, HTTP binding, the interop helpers.
/// </summary>
public sealed class NullabilityRulesTests
{
    [Fact]
    public void No_assembly_has_nullable_signatures_outside_the_edge()
    {
        var violations = Solution.All.SelectMany(NullabilityScanner.Violations).ToList();

        Assert.True(violations.Count == 0, $"Nullable in signatures (use Maybe<T>):{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    [Fact]
    public void Scanner_detects_nullable_signatures()
    {
        // Guards the guard: the sample below is nullable in every position the scanner checks.
        var violations = NullabilityScanner.Violations(typeof(NullabilityRulesTests).Assembly).ToList();

        Assert.Contains($"{typeof(Sample).FullName}.{nameof(Sample.Field)}", violations);
        Assert.Contains($"{typeof(Sample).FullName}.{nameof(Sample.Property)}", violations);
        Assert.Contains($"{typeof(Sample).FullName}.{nameof(Sample.Nested)}", violations);
        Assert.Contains($"{typeof(Sample).FullName}.{nameof(Sample.Method)} (return)", violations);
        Assert.Contains($"{typeof(Sample).FullName}.{nameof(Sample.Method)} (count)", violations);
        Assert.Contains($"{typeof(Sample).FullName}..ctor (name)", violations);
        Assert.DoesNotContain(violations, v => v.Contains(nameof(Sample.Equals), StringComparison.Ordinal));
    }

    internal sealed class Sample(string? name)
    {
        public string? Field = name;

        public string? Property { get; } = name;

        public IReadOnlyList<string?> Nested { get; } = [];

        public static string? Method(int? count) => count?.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public override bool Equals(object? obj) => ReferenceEquals(this, obj);

        public override int GetHashCode() => 0;
    }
}
