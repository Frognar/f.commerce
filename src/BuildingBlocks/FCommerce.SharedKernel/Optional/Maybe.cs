using FCommerce.SharedKernel.Results;

namespace FCommerce.SharedKernel.Optional;

/// <summary>
/// A value that may be absent — used everywhere instead of <c>null</c>. The value is reachable only through
/// <see cref="Match{TOut}"/> (and combinators built on it), so an absent value can never be read as a present one.
/// A struct: a <see cref="Maybe{T}"/> itself is never null, and <c>default</c> is <see cref="None"/>
/// (so a property missing from stored JSON reads as absent).
/// </summary>
public readonly record struct Maybe<T>
    where T : notnull
{
    private readonly T value;
    private readonly bool present;

    private Maybe(T value)
    {
        this.value = value;
        // A null smuggled in through code without nullable annotations (JSON, reflection) is still absent.
        present = value is not null;
    }

    public static Maybe<T> None => default;

    public static implicit operator Maybe<T>(T value) => new(value);

    public TOut Match<TOut>(Func<T, TOut> onSome, Func<TOut> onNone)
        where TOut : notnull =>
        present ? onSome(value) : onNone();

    public void Switch(Action<T> onSome, Action onNone)
    {
        if (present)
        {
            onSome(value);
        }
        else
        {
            onNone();
        }
    }

    public Maybe<TOut> Map<TOut>(Func<T, TOut> map)
        where TOut : notnull =>
        present ? map(value) : Maybe<TOut>.None;

    public Maybe<TOut> Bind<TOut>(Func<T, Maybe<TOut>> bind)
        where TOut : notnull =>
        present ? bind(value) : Maybe<TOut>.None;

    /// <summary>Keeps the value when it satisfies the predicate.</summary>
    public Maybe<T> Where(Func<T, bool> predicate) => present && predicate(value) ? this : None;

    /// <summary>True when the value is present and satisfies the predicate.</summary>
    public bool Exists(Func<T, bool> predicate) => present && predicate(value);

    public Maybe<T> Or(Maybe<T> alternative) => present ? this : alternative;

    public Maybe<T> Or(Func<Maybe<T>> alternative) => present ? this : alternative();

    public T ValueOr(T fallback) => present ? value : fallback;

    public T ValueOr(Func<T> fallback) => present ? value : fallback();

    public Result<T, TError> ToResult<TError>(Func<TError> error)
        where TError : notnull =>
        present ? Result.Ok<T, TError>(value) : Result.Fail<T, TError>(error());

    /// <summary>Zero or one element — handy for <c>foreach</c> in views.</summary>
    public IEnumerable<T> AsEnumerable() => present ? [value] : [];

    public override string ToString() => present ? $"Some({value})" : "None";
}

public static class Maybe
{
    public static Maybe<T> Some<T>(T value)
        where T : notnull =>
        value;

    public static Maybe<T> None<T>()
        where T : notnull =>
        Maybe<T>.None;

    /// <summary>
    /// Parses an optional raw input: absent or blank → <see cref="Maybe{T}.None"/>, otherwise the parse result.
    /// </summary>
    public static Result<Maybe<T>, Error> ParseOptional<T>(Maybe<string> raw, Func<string, Result<T, Error>> parse)
        where T : notnull =>
        raw.Where(text => !string.IsNullOrWhiteSpace(text)).Match(
            text => parse(text).Map(Some),
            () => Result.Ok<Maybe<T>, Error>(Maybe<T>.None));

    public static Maybe<T> ToMaybe<T, TError>(this Result<T, TError> result)
        where T : notnull
        where TError : notnull =>
        result.Match(Some, _ => Maybe<T>.None);

    public static Maybe<T> FirstOrNone<T>(this IEnumerable<T> items)
        where T : notnull
    {
        foreach (var item in items)
        {
            return item;
        }

        return Maybe<T>.None;
    }

    public static Maybe<T> FirstOrNone<T>(this IEnumerable<T> items, Func<T, bool> predicate)
        where T : notnull =>
        items.Where(predicate).FirstOrNone();

    public static Maybe<TValue> GetValueOrNone<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key)
        where TKey : notnull
        where TValue : notnull =>
        dictionary.TryGetValue(key, out var value) ? value : Maybe<TValue>.None;

    /// <summary>The present values, in order.</summary>
    public static IEnumerable<T> Choose<T>(this IEnumerable<Maybe<T>> items)
        where T : notnull =>
        items.SelectMany(item => item.AsEnumerable());

    /// <summary>Maps every item and keeps the present results, in order.</summary>
    public static IEnumerable<TOut> Choose<T, TOut>(this IEnumerable<T> items, Func<T, Maybe<TOut>> map)
        where T : notnull
        where TOut : notnull =>
        items.Select(map).Choose();

    // LINQ query syntax: from a in ma from b in mb select f(a, b)
    public static Maybe<TOut> Select<T, TOut>(this Maybe<T> maybe, Func<T, TOut> map)
        where T : notnull
        where TOut : notnull =>
        maybe.Map(map);

    public static Maybe<TOut> SelectMany<T, TNext, TOut>(this Maybe<T> maybe, Func<T, Maybe<TNext>> bind, Func<T, TNext, TOut> project)
        where T : notnull
        where TNext : notnull
        where TOut : notnull =>
        maybe.Bind(value => bind(value).Map(next => project(value, next)));
}
