using FCommerce.SharedKernel.Validation;
using FCommerce.Testing;

namespace FCommerce.SharedKernel.Tests;

public sealed class EnumsTests
{
    [Theory]
    [InlineData("Exact")]
    [InlineData("exact")]
    [InlineData(" EXACT ")]
    public void Reads_a_declared_name_ignoring_case_and_spaces(string raw) =>
        Assert.Equal(Mode.Exact, Enums.Parse<Mode>(raw).ShouldSucceed());

    [Theory]
    [InlineData("1")]
    [InlineData("7")]
    [InlineData("Levels, Exact")]
    [InlineData("Unknown")]
    [InlineData("")]
    public void Rejects_numbers_combinations_and_unknown_names(string raw) =>
        Assert.Equal("enum.unknown", Enums.Parse<Mode>(raw).ShouldFail().Code);

    private enum Mode
    {
        Levels,
        Exact,
    }
}
