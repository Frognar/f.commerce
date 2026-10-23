using FCommerce.Catalog.Domain;
using FCommerce.Testing;
using static FCommerce.Catalog.Tests.TestProducts;

namespace FCommerce.Catalog.Tests;

public sealed class ProductValidationTests
{
    [Fact]
    public void Valid_input_becomes_a_product()
    {
        var product = Valid();

        Assert.Equal("SR-M8-40", product.Sku.Value);
        Assert.Equal("M8x40 bolt", product.Content.Name.Text.Resolve(Values.Culture("en")));
        Assert.Equal("fasteners", product.Content.Category?.Value);
        Assert.Equal("5901234123457", product.Content.Gtin?.Value);
        Assert.Single(product.Content.Attributes);
    }

    [Fact]
    public void Reports_every_invalid_field_with_its_path()
    {
        var input = Input() with
        {
            Sku = " ",
            Version = -1,
            Name = new Dictionary<string, string> { ["english"] = "x" },
            Unit = null,
            Gtin = "5901234123450",
            ImageUrls = ["ftp://example.com/a.jpg", "https://ok.example.com/b.jpg"],
            Attributes = [new UnvalidatedAttribute("", null, null)],
        };

        var fields = Fields(Product.Validate(input));

        Assert.Equal(
            ["sku", "version", "name.english", "unit", "gtin", "imageUrls[0]", "attributes[0].code"],
            fields);
    }

    [Fact]
    public void Name_needs_at_least_one_translation()
    {
        var fields = Fields(Product.Validate(Input() with { Name = new Dictionary<string, string>() }));

        Assert.Equal(["name"], fields);
    }

    [Fact]
    public void Blank_optional_fields_are_not_set()
    {
        var product = Product.Validate(Input() with { CategoryCode = " ", Gtin = "", Description = null, ImageUrls = null, Attributes = null }).ShouldBeValid();

        Assert.Null(product.Content.Category);
        Assert.Null(product.Content.Gtin);
        Assert.True(product.Content.Description.IsEmpty);
        Assert.True(product.Content.Images.IsEmpty);
    }

    [Fact]
    public void Limits_the_number_of_images()
    {
        var fields = Fields(Product.Validate(Input() with { ImageUrls = [.. Enumerable.Repeat("https://cdn.example.com/a.jpg", Product.MaxImages + 1)] }));

        Assert.Equal(["imageUrls"], fields);
    }

    [Theory]
    [InlineData("5901234123457")] // EAN-13
    [InlineData("96385074")] // EAN-8
    [InlineData("036000291452")] // UPC-A
    [InlineData("10012345678902")] // GTIN-14
    public void Accepts_gtins_with_a_valid_check_digit(string gtin)
    {
        Assert.Equal(gtin, Gtin.Parse(gtin).ShouldSucceed().Value);
    }

    [Theory]
    [InlineData("5901234123458", "gtin.check-digit")]
    [InlineData("590123412345", "gtin.check-digit")]
    [InlineData("59012341234", "gtin.invalid")]
    [InlineData("59012341234AB", "gtin.invalid")]
    public void Rejects_invalid_gtins(string gtin, string code)
    {
        Assert.Equal(code, Gtin.Parse(gtin).ShouldFail().Code);
    }

    [Fact]
    public void Codes_are_trimmed_and_bounded()
    {
        Assert.Equal("ABC 1/2", Sku.Parse("  ABC 1/2 ").ShouldSucceed().Value);
        Assert.Equal("sku.too-long", Sku.Parse(new string('x', Sku.MaxLength + 1)).ShouldFail().Code);
        Assert.Equal("sku.invalid", Sku.Parse("A\tB").ShouldFail().Code);
    }

    [Fact]
    public void Category_cannot_be_its_own_parent()
    {
        var category = Category.Validate(new UnvalidatedCategory("tools", "tools", new Dictionary<string, string> { ["pl"] = "Narzędzia" }, 0, 1, true));

        Assert.Equal(["parentCode"], Fields(category));
    }
}
