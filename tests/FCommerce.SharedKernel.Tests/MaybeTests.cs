using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;
using FCommerce.Testing;

namespace FCommerce.SharedKernel.Tests;

public sealed class MaybeTests
{
    private static readonly Maybe<int> One = 1;
    private static readonly Maybe<int> None = Maybe<int>.None;

    [Fact]
    public void Match_selects_the_branch_of_presence()
    {
        Assert.Equal("some:1", One.Match(v => $"some:{v}", () => "none"));
        Assert.Equal("none", None.Match(v => $"some:{v}", () => "none"));
    }

    [Fact]
    public void Default_is_none_and_never_null()
    {
        Maybe<string> unset = default;

        Assert.Equal(Maybe<string>.None, unset);
        Assert.Equal("none", unset.Match(v => v, () => "none"));
    }

    [Fact]
    public void A_null_smuggled_in_is_none()
    {
        // Code without nullable annotations (reflection, JSON) can still hand over a null.
        Maybe<string> fromNull = Maybe.Some<string>(null!);

        Assert.Equal(Maybe<string>.None, fromNull);
    }

    [Fact]
    public void Equality_is_by_value()
    {
        Assert.Equal(Maybe.Some("a"), Maybe.Some("a"));
        Assert.NotEqual(Maybe.Some("a"), Maybe.Some("b"));
        Assert.NotEqual(Maybe.Some("a"), Maybe<string>.None);
    }

    [Fact]
    public void Map_and_bind_skip_the_absent_value()
    {
        Assert.Equal(Maybe.Some(2), One.Map(v => v + 1));
        Assert.Equal(None, None.Map(v => v + 1));
        Assert.Equal(Maybe.Some("1"), One.Bind(v => Maybe.Some(v.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        Assert.Equal(Maybe<string>.None, One.Bind(_ => Maybe<string>.None));
    }

    [Fact]
    public void Monad_laws_hold()
    {
        Maybe<int> Half(int v) => v % 2 == 0 ? v / 2 : Maybe<int>.None;
        Maybe<int> Decrement(int v) => v > 0 ? v - 1 : Maybe<int>.None;

        foreach (var value in new[] { 0, 1, 4, 7 })
        {
            Assert.Equal(Half(value), Maybe.Some(value).Bind(Half)); // left identity
        }

        foreach (var maybe in new[] { One, None, Maybe.Some(4) })
        {
            Assert.Equal(maybe, maybe.Bind(Maybe.Some)); // right identity
            Assert.Equal(maybe.Bind(Half).Bind(Decrement), maybe.Bind(v => Half(v).Bind(Decrement))); // associativity
        }
    }

    [Fact]
    public void Where_or_and_value_or_fall_back_when_absent()
    {
        Assert.Equal(None, One.Where(v => v > 1));
        Assert.Equal(One, None.Or(One));
        Assert.Equal(One, One.Or(() => Maybe.Some(5)));
        Assert.Equal(7, None.ValueOr(7));
        Assert.Equal(1, One.ValueOr(() => 7));
        Assert.True(One.Exists(v => v == 1));
        Assert.False(None.Exists(_ => true));
    }

    [Fact]
    public void Query_syntax_combines_values()
    {
        var both = from a in One from b in Maybe.Some(2) select a + b;
        var missing = from a in One from b in None select a + b;

        Assert.Equal(Maybe.Some(3), both);
        Assert.Equal(None, missing);
    }

    [Fact]
    public void Converts_to_result_and_back()
    {
        Assert.Equal(1, One.ToResult(() => Error.NotFound("x", "x")).ShouldSucceed());
        Assert.Equal("x", None.ToResult(() => Error.NotFound("x", "x")).ShouldFail().Code);
        Assert.Equal(One, Result.Ok<int, Error>(1).ToMaybe());
        Assert.Equal(None, Result.Fail<int, Error>(Error.NotFound("x", "x")).ToMaybe());
    }

    [Fact]
    public void Collections_yield_present_values()
    {
        Assert.Equal([1], One.AsEnumerable());
        Assert.Empty(None.AsEnumerable());
        Assert.Equal([1, 3], new[] { One, None, Maybe.Some(3) }.Choose());
        Assert.Equal(Maybe.Some(2), new[] { 1, 2, 3 }.FirstOrNone(v => v > 1));
        Assert.Equal(Maybe<int>.None, Array.Empty<int>().FirstOrNone());
        Assert.Equal(Maybe.Some("b"), new Dictionary<int, string> { [1] = "b" }.GetValueOrNone(1));
        Assert.Equal(Maybe<string>.None, new Dictionary<int, string>().GetValueOrNone(1));
    }

    [Fact]
    public void Optional_input_is_absent_when_blank_and_parsed_otherwise()
    {
        static Result<int, Error> Parse(string raw) =>
            int.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : Error.Validation("number.invalid", "x");

        Assert.Equal(Maybe<int>.None, Maybe.ParseOptional(Maybe<string>.None, Parse).ShouldSucceed());
        Assert.Equal(Maybe<int>.None, Maybe.ParseOptional(" ", Parse).ShouldSucceed());
        Assert.Equal(Maybe.Some(5), Maybe.ParseOptional("5", Parse).ShouldSucceed());
        Assert.Equal("number.invalid", Maybe.ParseOptional("x", Parse).ShouldFail().Code);
    }

    [Fact]
    public void Nullables_convert_at_the_edge()
    {
        string? missing = null;
        int? count = 3;

        Assert.Equal(Maybe<string>.None, missing.ToMaybe());
        Assert.Equal(Maybe.Some(3), count.ToMaybe());
        Assert.Null(Maybe<string>.None.OrNull());
        Assert.Equal(3, Maybe.Some(3).OrNull());
        Assert.Equal(string.Empty, missing.OrEmpty());
    }

    [Fact]
    public void Error_fields_compose_without_null()
    {
        var error = Error.Validation("x", "x").AtField("email").AtField("[2]").AtField("contacts");

        Assert.Equal("contacts[2].email", error.Field.ShouldBeSome());
        Assert.Equal(Maybe<string>.None, Error.Validation("x", "x").Field);
        Assert.Equal("x", Validated.Invalid<int>(Error.Validation("x", "x")).ShouldBeInvalid()[0].Code);
    }
}
