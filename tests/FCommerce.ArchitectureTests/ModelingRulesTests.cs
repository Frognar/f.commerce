using FCommerce.SharedKernel.Validation;

namespace FCommerce.ArchitectureTests;

/// <summary>
/// Invalid states are unrepresentable: a domain value exists only when it is valid. Text and numbers become domain
/// values through <c>Parse</c>/<c>Validate</c> (a smart constructor behind a non-public constructor); public
/// constructors take only values that are valid already. See <see cref="ModelingScanner"/>.
/// </summary>
public sealed class ModelingRulesTests
{
    /// <summary>Types allowed to take text or numbers for good, with the reason.</summary>
    private static readonly Dictionary<string, string> Exempt = new()
    {
        ["FCommerce.SharedKernel.Results.Error"] = "a diagnostic: any code and message is a valid error",
        ["FCommerce.SharedKernel.Monetary.Money"] = "every amount in a currency is money (sign and scale are rules of its users)",
        ["FCommerce.SharedKernel.Features.Feature"] = "the feature catalogue is defined in code, its description is free text",
        ["FCommerce.SharedKernel.Imports.Rejection"] = "reports a position and key of the raw input",
        ["FCommerce.SharedKernel.Imports.ValidatedBatch`1"] = "pairs valid items with their position in the raw input",
        ["FCommerce.Authorization.Core.Decision+Denied"] = "reasons are diagnostics",
        ["FCommerce.Authorization.Core.PolicyResult+DenyResult"] = "the reason is a diagnostic",
        ["FCommerce.Authorization.Core.Subject+CustomerUser"] = "a shared building block: customer codes stay text here (value types are per module)",
        ["FCommerce.Authorization.Core.Resource+ApiKeys"] = "customer codes stay text in the shared building block",
        ["FCommerce.Authorization.Core.Resource+Cart"] = "customer codes stay text in the shared building block",
        ["FCommerce.Authorization.Core.Resource+CustomerDocuments"] = "customer codes stay text in the shared building block",
        ["FCommerce.Authorization.Core.Resource+CustomerProfile"] = "customer codes stay text in the shared building block",
        ["FCommerce.Authorization.Core.Resource+CustomerUsers"] = "customer codes stay text in the shared building block",
        ["FCommerce.Authorization.Core.Resource+Order"] = "customer codes stay text in the shared building block",
        ["FCommerce.Authorization.Core.Resource+Orders"] = "customer codes stay text in the shared building block",
        ["FCommerce.Authorization.Core.Resource+ShoppingLists"] = "customer codes stay text in the shared building block",
        ["FCommerce.Authorization.Core.DeliveryAddressScope+OnlyAddresses"] = "address codes stay text in the shared building block (never empty: NonEmptyArray)",
        ["FCommerce.Catalog.Domain.SearchEntry"] = "a document of the search index: texts already resolved from a valid product",
        ["FCommerce.Catalog.Domain.Facet"] = "built from search results for display",
        ["FCommerce.Catalog.Domain.FacetValue"] = "built from search results for display",
        ["FCommerce.Cart.Domain.PastedLineError"] = "reports a line of the raw pasted text",
        ["FCommerce.Inventory.Domain.WarehouseContent"] = "the sort order: any number orders warehouses",
        ["FCommerce.Catalog.Domain.CategoryContent"] = "the sort order: any number orders categories",
    };

    /// <summary>
    /// Types still to be remodelled. A fixed type must leave the list (the test fails otherwise), so the list only
    /// shrinks; every phase of the refactoring is done, so it stays empty.
    /// </summary>
    private static readonly string[] KnownViolations = [];

    [Fact]
    public void Domain_types_are_valid_by_construction()
    {
        var violations = ModelingScanner.Scope
            .SelectMany(ModelingScanner.Violations)
            .Where(v => !Exempt.ContainsKey(v.Type))
            .GroupBy(v => v.Type)
            .ToDictionary(g => g.Key, g => string.Join("; ", g.Select(v => v.Detail)));

        var unexpected = violations.Keys.Except(KnownViolations).Order(StringComparer.Ordinal).Select(type => $"{type}: {violations[type]}").ToList();
        var fixedTypes = KnownViolations.Except(violations.Keys).ToList();

        Assert.True(
            unexpected.Count == 0,
            $"Domain types that can be built invalid (take a validated type, or hide the constructor behind Parse/Validate; [RawInput] for raw input):{Environment.NewLine}{string.Join(Environment.NewLine, unexpected)}");
        Assert.True(fixedTypes.Count == 0, $"Remodelled — remove from {nameof(KnownViolations)}:{Environment.NewLine}{string.Join(Environment.NewLine, fixedTypes)}");
    }

    [Fact]
    public void Exemptions_name_existing_types()
    {
        var types = ModelingScanner.Scope.SelectMany(a => a.GetTypes()).Select(t => t.FullName).ToHashSet();

        var stale = Exempt.Keys.Where(type => !types.Contains(type)).ToList();

        Assert.True(stale.Count == 0, $"Exempted types that no longer exist:{Environment.NewLine}{string.Join(Environment.NewLine, stale)}");
    }

    [Fact]
    public void Scanner_detects_types_that_can_be_built_invalid()
    {
        // Guards the guard: the samples below cover every rule and its exemptions.
        var violations = ModelingScanner.Violations(typeof(ModelingRulesTests).Assembly).Select(v => v.Type).ToList();

        Assert.Contains(typeof(TextInConstructor).FullName, violations);
        Assert.Contains(typeof(NumberInMaybe).FullName, violations);
        Assert.Contains(typeof(InitBehindPrivateConstructor).FullName, violations);
        Assert.Contains(typeof(PublicTextSetter).FullName, violations);
        Assert.DoesNotContain(typeof(SmartConstructed).FullName, violations);
        Assert.DoesNotContain(typeof(RawSample).FullName, violations);
        Assert.DoesNotContain(typeof(RawSample.Nested).FullName, violations);
        Assert.DoesNotContain(typeof(OfValidParts).FullName, violations);
    }

    public sealed record TextInConstructor(string Code);

    public sealed record NumberInMaybe(SharedKernel.Optional.Maybe<decimal> Quantity);

    public sealed record OfValidParts(SmartConstructed Part, DateTimeOffset At, Guid Id);

    public sealed record InitBehindPrivateConstructor
    {
        private InitBehindPrivateConstructor(Guid id) => Id = id;

        public Guid Id { get; init; }
    }

    public sealed class PublicTextSetter
    {
        public string Text { get; set; } = string.Empty;
    }

    public sealed record SmartConstructed
    {
        private SmartConstructed(string value) => Value = value;

        public string Value { get; }

        public static SmartConstructed Parse(string raw) => new(raw.Trim());
    }

    [RawInput]
    public sealed record RawSample(string Code, decimal Quantity)
    {
        public sealed record Nested(string Text);
    }
}
