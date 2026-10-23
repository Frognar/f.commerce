namespace FCommerce.SharedKernel.Results;

/// <summary>
/// Either a success value or an error. The value and the error are reachable only through
/// <see cref="Match{TOut}"/> (and combinators built on it), so a failure can never be read as a success.
/// </summary>
public abstract record Result<T, TError>
{
    private Result()
    {
    }

    public static implicit operator Result<T, TError>(T value) => new Success(value);

    public static implicit operator Result<T, TError>(TError error) => new Failure(error);

    public abstract TOut Match<TOut>(Func<T, TOut> onSuccess, Func<TError, TOut> onFailure);

    public void Switch(Action<T> onSuccess, Action<TError> onFailure) =>
        Match(
            value =>
            {
                onSuccess(value);
                return Unit.Value;
            },
            error =>
            {
                onFailure(error);
                return Unit.Value;
            });

    public Result<TOut, TError> Map<TOut>(Func<T, TOut> map) =>
        Match(value => Result<TOut, TError>.Ok(map(value)), Result<TOut, TError>.Fail);

    public Result<TOut, TError> Bind<TOut>(Func<T, Result<TOut, TError>> bind) =>
        Match(bind, Result<TOut, TError>.Fail);

    public Result<T, TOut> MapError<TOut>(Func<TError, TOut> map) =>
        Match(Result<T, TOut>.Ok, error => Result<T, TOut>.Fail(map(error)));

    /// <summary>Keeps the value when it satisfies the predicate, otherwise fails with the given error.</summary>
    public Result<T, TError> Ensure(Func<T, bool> predicate, Func<T, TError> error) =>
        Bind(value => predicate(value) ? Ok(value) : Fail(error(value)));

    /// <summary>Recovers from a failure with a fallback value computed from the error.</summary>
    public T ValueOr(Func<TError, T> fallback) => Match(value => value, fallback);

    internal static Result<T, TError> Ok(T value) => new Success(value);

    internal static Result<T, TError> Fail(TError error) => new Failure(error);

    private sealed record Success(T Value) : Result<T, TError>
    {
        public override TOut Match<TOut>(Func<T, TOut> onSuccess, Func<TError, TOut> onFailure) => onSuccess(Value);

        public override string ToString() => $"Ok({Value})";
    }

    private sealed record Failure(TError Error) : Result<T, TError>
    {
        public override TOut Match<TOut>(Func<T, TOut> onSuccess, Func<TError, TOut> onFailure) => onFailure(Error);

        public override string ToString() => $"Fail({Error})";
    }
}

public static class Result
{
    public static Result<T, TError> Ok<T, TError>(T value) => Result<T, TError>.Ok(value);

    public static Result<T, TError> Fail<T, TError>(TError error) => Result<T, TError>.Fail(error);

    /// <summary>Collects all values, or returns the first error.</summary>
    public static Result<IReadOnlyList<T>, TError> Sequence<T, TError>(this IEnumerable<Result<T, TError>> results) =>
        results.Aggregate(
            Ok<IReadOnlyList<T>, TError>([]),
            (all, next) => all.Bind(values => next.Map(value => (IReadOnlyList<T>)[.. values, value])));

    /// <summary>Applies a fallible function to every item; all values or the first error.</summary>
    public static Result<IReadOnlyList<TOut>, TError> Traverse<T, TOut, TError>(this IEnumerable<T> items, Func<T, Result<TOut, TError>> map) =>
        items.Select(map).Sequence();

    // LINQ query syntax: from a in ra from b in rb select f(a, b)
    public static Result<TOut, TError> Select<T, TOut, TError>(this Result<T, TError> result, Func<T, TOut> map) =>
        result.Map(map);

    public static Result<TOut, TError> SelectMany<T, TNext, TOut, TError>(
        this Result<T, TError> result,
        Func<T, Result<TNext, TError>> bind,
        Func<T, TNext, TOut> project) =>
        result.Bind(value => bind(value).Map(next => project(value, next)));
}
