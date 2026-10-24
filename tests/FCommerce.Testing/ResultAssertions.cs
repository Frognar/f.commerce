using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;

namespace FCommerce.Testing;

/// <summary>
/// Unwrapping a <see cref="Result{T,TError}"/>, <see cref="Validated{T}"/> or <see cref="Maybe{T}"/> is allowed only in
/// tests: a wrong outcome fails the test. Production code must handle both cases explicitly (Match / Bind / Map).
/// </summary>
public static class ResultAssertions
{
    public static T ShouldSucceed<T, TError>(this Result<T, TError> result)
        where T : notnull
        where TError : notnull =>
        result.Match(value => value, error => throw new ResultAssertionException($"Expected success but got failure: {error}"));

    public static TError ShouldFail<T, TError>(this Result<T, TError> result)
        where T : notnull
        where TError : notnull =>
        result.Match(value => throw new ResultAssertionException($"Expected failure but got success: {value}"), error => error);

    public static async Task<T> ShouldSucceed<T, TError>(this Task<Result<T, TError>> result)
        where T : notnull
        where TError : notnull =>
        (await result).ShouldSucceed();

    public static T ShouldBeValid<T>(this Validated<T> validated)
        where T : notnull =>
        validated.Match(value => value, errors => throw new ResultAssertionException($"Expected a valid value but got: {errors}"));

    public static EquatableArray<Error> ShouldBeInvalid<T>(this Validated<T> validated)
        where T : notnull =>
        validated.Match(value => throw new ResultAssertionException($"Expected errors but got: {value}"), errors => errors);

    public static async Task<TError> ShouldFail<T, TError>(this Task<Result<T, TError>> result)
        where T : notnull
        where TError : notnull =>
        (await result).ShouldFail();

    public static T ShouldBeSome<T>(this Maybe<T> maybe)
        where T : notnull =>
        maybe.Match(value => value, () => throw new ResultAssertionException("Expected a value but got none."));

    public static void ShouldBeNone<T>(this Maybe<T> maybe)
        where T : notnull =>
        maybe.Switch(value => throw new ResultAssertionException($"Expected none but got: {value}"), () => { });
}

public sealed class ResultAssertionException(string message) : Exception(message);
