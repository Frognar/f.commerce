using FCommerce.SharedKernel.Results;

namespace FCommerce.Application.Results;

/// <summary>
/// Async combinators for <see cref="Result{T,TError}"/>. They live in the shell, not the core:
/// the functional core is synchronous, IO (and therefore Task) happens only at the edges.
/// </summary>
public static class AsyncResult
{
    public static async Task<Result<TOut, TError>> MapAsync<T, TOut, TError>(this Result<T, TError> result, Func<T, Task<TOut>> map) =>
        await result.Match(
            async value => Result.Ok<TOut, TError>(await map(value)),
            error => Task.FromResult(Result.Fail<TOut, TError>(error)));

    public static Task<Result<TOut, TError>> BindAsync<T, TOut, TError>(this Result<T, TError> result, Func<T, Task<Result<TOut, TError>>> bind) =>
        result.Match(bind, error => Task.FromResult(Result.Fail<TOut, TError>(error)));

    public static Task<TOut> MatchAsync<T, TError, TOut>(this Result<T, TError> result, Func<T, Task<TOut>> onSuccess, Func<TError, Task<TOut>> onFailure) =>
        result.Match(onSuccess, onFailure);

    public static async Task<Result<TOut, TError>> Map<T, TOut, TError>(this Task<Result<T, TError>> result, Func<T, TOut> map) =>
        (await result).Map(map);

    public static async Task<Result<TOut, TError>> Bind<T, TOut, TError>(this Task<Result<T, TError>> result, Func<T, Result<TOut, TError>> bind) =>
        (await result).Bind(bind);

    public static async Task<Result<TOut, TError>> MapAsync<T, TOut, TError>(this Task<Result<T, TError>> result, Func<T, Task<TOut>> map) =>
        await (await result).MapAsync(map);

    public static async Task<Result<TOut, TError>> BindAsync<T, TOut, TError>(this Task<Result<T, TError>> result, Func<T, Task<Result<TOut, TError>>> bind) =>
        await (await result).BindAsync(bind);

    public static async Task<TOut> Match<T, TError, TOut>(this Task<Result<T, TError>> result, Func<T, TOut> onSuccess, Func<TError, TOut> onFailure) =>
        (await result).Match(onSuccess, onFailure);

    public static async Task<TOut> MatchAsync<T, TError, TOut>(this Task<Result<T, TError>> result, Func<T, Task<TOut>> onSuccess, Func<TError, Task<TOut>> onFailure) =>
        await (await result).Match(onSuccess, onFailure);
}
