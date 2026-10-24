using FCommerce.SharedKernel.Contact;
using FCommerce.Testing;

namespace FCommerce.SharedKernel.Tests;

public sealed class EmailAddressTests
{
    [Theory]
    [InlineData(" Jan.Kowalski@Firma.PL ", "jan.kowalski@firma.pl")]
    [InlineData("a+tag@sub.example.com", "a+tag@sub.example.com")]
    public void Normalizes_valid_addresses(string input, string expected)
    {
        var email = EmailAddress.Parse(input).ShouldSucceed();

        Assert.Equal(expected, email.Value);
        Assert.Equal(EmailAddress.Parse(expected).ShouldSucceed(), email);
    }

    [Theory]
    [InlineData("")]
    [InlineData("jan")]
    [InlineData("jan@firma")]
    [InlineData("jan@@firma.pl")]
    [InlineData("jan kowalski@firma.pl")]
    [InlineData("jan@firma..pl")]
    [InlineData("@firma.pl")]
    [InlineData("<jan@firma.pl>")]
    public void Rejects_malformed_addresses(string input)
    {
        Assert.Equal("email.invalid", EmailAddress.Parse(input).ShouldFail().Code);
    }
}

public sealed class CountryCodeTests
{
    [Theory]
    [InlineData("pl", "PL")]
    [InlineData(" de ", "DE")]
    public void Accepts_assigned_codes(string input, string expected)
    {
        Assert.Equal(expected, CountryCode.Parse(input).ShouldSucceed().Value);
    }

    [Theory]
    [InlineData("XX")]
    [InlineData("POL")]
    [InlineData("")]
    public void Rejects_unassigned_codes(string input)
    {
        Assert.Equal("country.invalid", CountryCode.Parse(input).ShouldFail().Code);
    }
}
