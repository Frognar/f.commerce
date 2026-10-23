using FCommerce.SharedKernel.Results;
using FCommerce.Testing;

namespace FCommerce.SharedKernel.Tests;

public sealed class ResultTests
{
    private static readonly Error Invalid = Error.Validation("invalid", "x");

    private static Result<int, Error> Ok(int value) => value;

    private static Result<int, Error> Fail(Error error) => error;

    [Fact]
    public void Match_selects_the_branch_of_the_outcome()
    {
        Assert.Equal("ok:1", Ok(1).Match(v => $"ok:{v}", e => $"fail:{e.Code}"));
        Assert.Equal("fail:invalid", Fail(Invalid).Match(v => $"ok:{v}", e => $"fail:{e.Code}"));
    }

    [Fact]
    public void Switch_runs_exactly_one_action()
    {
        var log = new List<string>();

        Ok(1).Switch(v => log.Add($"ok:{v}"), e => log.Add("fail"));
        Fail(Invalid).Switch(v => log.Add("ok"), e => log.Add($"fail:{e.Code}"));

        Assert.Equal(["ok:1", "fail:invalid"], log);
    }

    [Fact]
    public void Map_and_bind_short_circuit_on_failure()
    {
        var calls = 0;

        var result = Ok(1)
            .Bind(_ => Fail(Invalid))
            .Map(v => { calls++; return v + 1; })
            .Bind(v => { calls++; return Ok(v); });

        Assert.Equal(Invalid, result.ShouldFail());
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Map_error_transforms_only_failures()
    {
        Assert.Equal(1, Ok(1).MapError(e => e.Code).ShouldSucceed());
        Assert.Equal("invalid", Fail(Invalid).MapError(e => e.Code).ShouldFail());
    }

    [Fact]
    public void Ensure_turns_a_rejected_value_into_an_error()
    {
        var positive = (Result<int, Error> r) => r.Ensure(v => v > 0, v => Error.Validation("not-positive", $"{v}"));

        Assert.Equal(1, positive(Ok(1)).ShouldSucceed());
        Assert.Equal("not-positive", positive(Ok(-1)).ShouldFail().Code);
        Assert.Equal(Invalid, positive(Fail(Invalid)).ShouldFail());
    }

    [Fact]
    public void Value_or_recovers_from_failure()
    {
        Assert.Equal(1, Ok(1).ValueOr(_ => 0));
        Assert.Equal(0, Fail(Invalid).ValueOr(_ => 0));
    }

    [Fact]
    public void Sequence_and_traverse_collect_values_or_return_the_first_error()
    {
        Result<int, Error>[] ok = [1, 2];
        Result<int, Error>[] failed = [1, Error.NotFound("missing", "x"), Error.Conflict("later", "y")];

        Assert.Equal([1, 2], ok.Sequence().ShouldSucceed());
        Assert.Equal("missing", failed.Sequence().ShouldFail().Code);
        Assert.Equal([2, 4], ok.Select(r => r.ValueOr(_ => 0)).Traverse(v => Ok(v * 2)).ShouldSucceed());
        Assert.Empty(Array.Empty<Result<int, Error>>().Sequence().ShouldSucceed());
    }

    [Fact]
    public void Query_syntax_composes_results()
    {
        var sum = from a in Ok(1) from b in Ok(2) select a + b;
        var failed = from a in Ok(1) from b in Fail(Invalid) select a + b;

        Assert.Equal(3, sum.ShouldSucceed());
        Assert.Equal(Invalid, failed.ShouldFail());
    }

    [Fact]
    public void Results_compare_by_value()
    {
        Assert.Equal(Ok(1), Ok(1));
        Assert.NotEqual(Ok(1), Ok(2));
        Assert.Equal(Fail(Invalid), Fail(Error.Validation("invalid", "x")));
        Assert.NotEqual(Ok(1), Fail(Invalid));
    }
}
