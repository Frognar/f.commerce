using FCommerce.SharedKernel.Results;

namespace FCommerce.Testing;

/// <summary>
/// Unwrapping a <see cref="Result{T,TError}"/> is allowed only in tests: a wrong outcome fails the test.
/// Production code must handle both cases explicitly (Match / Bind / Map).
/// </summary>
public static class ResultAssertions
{
    public static T ShouldSucceed<T, TError>(this Result<T, TError> result) =>
        result.Match(value => value, error => throw new ResultAssertionException($"Expected success but got failure: {error}"));

    public static TError ShouldFail<T, TError>(this Result<T, TError> result) =>
        result.Match(value => throw new ResultAssertionException($"Expected failure but got success: {value}"), error => error);

    public static async Task<T> ShouldSucceed<T, TError>(this Task<Result<T, TError>> result) => (await result).ShouldSucceed();

    public static async Task<TError> ShouldFail<T, TError>(this Task<Result<T, TError>> result) => (await result).ShouldFail();
}

public sealed class ResultAssertionException(string message) : Exception(message);
