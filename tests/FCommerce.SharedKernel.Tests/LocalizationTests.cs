using FCommerce.SharedKernel.Localization;
using FCommerce.Testing;
using static FCommerce.Testing.Values;

namespace FCommerce.SharedKernel.Tests;

public sealed class CultureCodeTests
{
    [Theory]
    [InlineData("pl", "pl")]
    [InlineData("EN", "en")]
    [InlineData("en-gb", "en-GB")]
    [InlineData(" de_AT ", "de-AT")]
    [InlineData("sr-Latn", "sr-LATN")]
    public void Normalizes_valid_codes(string input, string expected)
    {
        Assert.Equal(expected, CultureCode.Parse(input).ShouldSucceed().Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("p")]
    [InlineData("polish")]
    [InlineData("en-")]
    [InlineData("en-GB-x")]
    [InlineData("e1")]
    public void Rejects_invalid_codes(string? input)
    {
        Assert.StartsWith("culture.", CultureCode.Parse(input).ShouldFail().Code, StringComparison.Ordinal);
    }

    [Fact]
    public void Language_of_a_regional_culture_is_its_neutral_part()
    {
        Assert.Equal(Culture("en"), Culture("en-GB").Language);
        Assert.Equal(Culture("en"), Culture("en").Language);
    }
}

public sealed class LocalizedTextTests
{
    private static readonly CultureCode Pl = Culture("pl");
    private static readonly CultureCode En = Culture("en");
    private static readonly CultureCode EnGb = Culture("en-GB");
    private static readonly CultureCode EnUs = Culture("en-US");
    private static readonly CultureCode De = Culture("de");

    [Fact]
    public void Resolves_exact_culture_first()
    {
        var text = LocalizedText.Of((En, "color"), (EnGb, "colour"));

        Assert.Equal("colour", text.Resolve(EnGb));
    }

    [Fact]
    public void Falls_back_to_neutral_language_then_to_a_sibling_region()
    {
        Assert.Equal("color", LocalizedText.Of((En, "color")).Resolve(EnGb));
        Assert.Equal("colour", LocalizedText.Of((EnGb, "colour")).Resolve(EnUs));
    }

    [Fact]
    public void Falls_back_to_the_given_cultures_in_order()
    {
        var text = LocalizedText.Of((Pl, "kolor"), (En, "color"));

        Assert.Equal("color", text.Resolve(De, En, Pl));
        Assert.Equal("kolor", text.Resolve(De, Pl, En));
        Assert.Null(LocalizedText.Of((Pl, "kolor")).Resolve(De, En));
    }

    [Fact]
    public void Blank_translation_removes_it()
    {
        var text = LocalizedText.Of((Pl, "kolor")).With(Pl, "  ");

        Assert.True(text.IsEmpty);
    }

    [Fact]
    public void Is_immutable_and_compared_by_value()
    {
        var original = LocalizedText.Of((Pl, "kolor"));
        var changed = original.With(En, "color");

        Assert.Equal(LocalizedText.Of((Pl, "kolor")), original);
        Assert.Equal(LocalizedText.Of((En, "color"), (Pl, "kolor")), changed);
        Assert.Equal(LocalizedText.Of((En, "color"), (Pl, "kolor")).GetHashCode(), changed.GetHashCode());
        Assert.NotEqual(original, changed);
    }
}
