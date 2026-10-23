using FCommerce.Catalog.Domain;
using FCommerce.SharedKernel.Validation;
using FCommerce.Testing;

namespace FCommerce.Catalog.Tests;

internal static class TestProducts
{
    public static readonly string ImageA = new('a', 64);
    public static readonly string ImageB = new('b', 64);

    public static UnvalidatedProduct Input(string sku = "SR-M8-40", long version = 1) =>
        new(
            sku,
            version,
            new Dictionary<string, string> { ["pl"] = "Śruba M8x40", ["en"] = "M8x40 bolt" },
            new Dictionary<string, string> { ["pl"] = "Ocynkowana" },
            "fasteners",
            "szt",
            "5901234123457",
            [ImageA, ImageB],
            [new UnvalidatedAttribute("material", new Dictionary<string, string> { ["pl"] = "Materiał" }, new Dictionary<string, string> { ["pl"] = "Stal" })],
            Active: true);

    public static Product Valid(string sku = "SR-M8-40", long version = 1) =>
        Product.Validate(Input(sku, version)).ShouldBeValid();

    public static IReadOnlyList<string?> Fields<T>(Validated<T> validated) =>
        [.. validated.ShouldBeInvalid().Select(e => e.Field)];
}
