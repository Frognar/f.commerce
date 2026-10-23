using FCommerce.Customers.Domain;
using FCommerce.SharedKernel.Contact;
using FCommerce.Testing;

namespace FCommerce.Customers.Tests;

public sealed class IdentifierTests
{
    private static CountryCode Country(string code) => CountryCode.Parse(code).ShouldSucceed();

    [Theory]
    [InlineData("5261040828", "PL5261040828")]
    [InlineData("526-104-08-28", "PL5261040828")]
    [InlineData("PL 526 104 08 28", "PL5261040828")]
    [InlineData("pl5261040828", "PL5261040828")]
    [InlineData("DE123456789", "DE123456789")]
    public void Tax_ids_are_normalized(string raw, string expected) =>
        Assert.Equal(expected, TaxId.Parse(raw).ShouldSucceed().Value);

    [Theory]
    [InlineData("5261040827", "tax-id.checksum")]
    [InlineData("PL526104082", "tax-id.checksum")]
    [InlineData("12345", "tax-id.invalid")]
    [InlineData("", "tax-id.invalid")]
    [InlineData("DE12/3456", "tax-id.invalid")]
    public void Invalid_tax_ids_are_refused(string raw, string code) =>
        Assert.Equal(code, TaxId.Parse(raw).ShouldFail().Code);

    [Fact]
    public void A_polish_nip_whose_checksum_is_ten_is_never_valid()
    {
        // 6·1 + 5·0 + … = 120 → 120 mod 11 = 10: no last digit can match.
        Assert.Equal("tax-id.checksum", TaxId.Parse("1000000000").ShouldFail().Code);
    }

    [Theory]
    [InlineData("00-950", "00-950")]
    [InlineData("00950", "00-950")]
    [InlineData(" 61-131 ", "61-131")]
    public void Polish_postal_codes_have_the_form_00_000(string raw, string expected) =>
        Assert.Equal(expected, PostalCode.Parse(raw, Country("PL")).ShouldSucceed().Value);

    [Theory]
    [InlineData("0-0950")]
    [InlineData("609-50")]
    [InlineData("6095")]
    [InlineData("61-1311")]
    public void Malformed_polish_postal_codes_are_refused(string raw) =>
        Assert.Equal("postal-code.invalid", PostalCode.Parse(raw, Country("PL")).ShouldFail().Code);

    [Theory]
    [InlineData("DE", "10115", "10115")]
    [InlineData("GB", "sw1a 1aa", "SW1A 1AA")]
    public void Other_countries_accept_alphanumeric_postal_codes(string country, string raw, string expected) =>
        Assert.Equal(expected, PostalCode.Parse(raw, Country(country)).ShouldSucceed().Value);
}
