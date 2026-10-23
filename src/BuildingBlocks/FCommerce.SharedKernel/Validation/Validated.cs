using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Results;

namespace FCommerce.SharedKernel.Validation;

/// <summary>
/// A valid value or a non-empty list of errors. Unlike <see cref="Result{T,TError}"/>, combining several
/// <see cref="Validated{T}"/> with <see cref="Validated.Apply{T1,TResult}(Validated{Func{T1,TResult}},Validated{T1})"/>
/// collects the errors of all of them instead of stopping at the first one.
/// The value and the errors are reachable only through <see cref="Match{TOut}"/>.
/// </summary>
public abstract record Validated<T>
{
    private Validated()
    {
    }

    public static implicit operator Validated<T>(T value) => new Valid(value);

    public static implicit operator Validated<T>(Error error) => new Invalid(EquatableArray.Create(error));

    public abstract TOut Match<TOut>(Func<T, TOut> onValid, Func<EquatableArray<Error>, TOut> onInvalid);

    public Validated<TOut> Map<TOut>(Func<T, TOut> map) =>
        Match(value => Validated<TOut>.Succeed(map(value)), Validated<TOut>.Fail);

    /// <summary>
    /// Sequential step for rules that need the whole, already valid value (e.g. cross-field rules).
    /// Runs only when valid, so it does not accumulate with earlier errors — use Apply for independent inputs.
    /// </summary>
    public Validated<TOut> Bind<TOut>(Func<T, Validated<TOut>> bind) =>
        Match(bind, Validated<TOut>.Fail);

    /// <summary>Keeps a valid value that satisfies the predicate, otherwise fails with the given error.</summary>
    public Validated<T> Ensure(Func<T, bool> predicate, Func<T, Error> error) =>
        Bind(value => predicate(value) ? Succeed(value) : Fail(EquatableArray.Create(error(value))));

    /// <summary>
    /// Like <see cref="Ensure"/> for several rules of a valid value, but reports every rule that fails
    /// (chained Ensure stops at the first).
    /// </summary>
    public Validated<T> EnsureAll(params (Func<T, bool> Holds, Error Error)[] rules) =>
        Bind(value => rules.Where(rule => !rule.Holds(value)).Select(rule => rule.Error).ToEquatableArray() is { IsEmpty: false } errors
            ? Fail(errors)
            : Succeed(value));

    /// <summary>Places all errors under a field (see <see cref="Error.AtField"/>).</summary>
    public Validated<T> AtField(string field) =>
        Match(Succeed, errors => Fail(errors.Select(e => e.AtField(field)).ToEquatableArray()));

    /// <summary>Back to the sequential world: several errors become one <see cref="Error.Invalid"/>.</summary>
    public Result<T, Error> ToResult() =>
        Match(Result.Ok<T, Error>, errors => Result.Fail<T, Error>(Error.Invalid(errors)));

    internal static Validated<T> Succeed(T value) => new Valid(value);

    /// <summary>Callers guarantee at least one error.</summary>
    internal static Validated<T> Fail(EquatableArray<Error> errors) => new Invalid(errors);

    private sealed record Valid(T Value) : Validated<T>
    {
        public override TOut Match<TOut>(Func<T, TOut> onValid, Func<EquatableArray<Error>, TOut> onInvalid) => onValid(Value);

        public override string ToString() => $"Valid({Value})";
    }

    private sealed record Invalid(EquatableArray<Error> Errors) : Validated<T>
    {
        public override TOut Match<TOut>(Func<T, TOut> onValid, Func<EquatableArray<Error>, TOut> onInvalid) => onInvalid(Errors);

        public override string ToString() => $"Invalid({Errors})";
    }
}
