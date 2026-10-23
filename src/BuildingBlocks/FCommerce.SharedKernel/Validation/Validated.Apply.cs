using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Results;

namespace FCommerce.SharedKernel.Validation;

/// <summary>
/// Creating and combining <see cref="Validated{T}"/> values. <c>Apply</c> applies a validated value to a
/// (validated) function, producing a validated function with one fewer parameter; errors of the function and
/// of the value are concatenated:
/// <code>
/// Validated&lt;Customer&gt; customer = Customer.Create
///     .Apply(TaxId.Parse(input.TaxId).ToValidated("taxId"))
///     .Apply(Email.Parse(input.Email).ToValidated("email"))
///     .Apply(CompanyName.Parse(input.Name).ToValidated("name"));
/// </code>
/// </summary>
public static class Validated
{
    public static Validated<T> Valid<T>(T value) => Validated<T>.Succeed(value);

    public static Validated<T> Invalid<T>(Error error, params ReadOnlySpan<Error> more) =>
        Validated<T>.Fail([error, .. more]);

    /// <summary>Lifts a sequential result; the error is placed under <paramref name="field"/> when given.</summary>
    public static Validated<T> ToValidated<T>(this Result<T, Error> result, string? field = null) =>
        result.Match(
            Validated<T>.Succeed,
            error => Validated<T>.Fail(Flatten(field is null ? error : error.AtField(field))));

    /// <summary>All values, or the errors of every invalid item.</summary>
    public static Validated<IReadOnlyList<T>> Sequence<T>(this IEnumerable<Validated<T>> items) =>
        items.Aggregate(
            Valid<IReadOnlyList<T>>([]),
            (all, next) => Combine(all, next, (values, value) => (IReadOnlyList<T>)[.. values, value]));

    /// <summary>Validates every item; errors are placed under the item index ("[2].quantity").</summary>
    public static Validated<IReadOnlyList<TOut>> Traverse<T, TOut>(this IEnumerable<T> items, Func<T, Validated<TOut>> validate) =>
        items.Select((item, index) => validate(item).AtField($"[{index}]")).Sequence();

    private static EquatableArray<Error> Flatten(Error error) =>
        error.Code == Error.ValidationFailedCode && !error.Details.IsEmpty ? error.Details : EquatableArray.Create(error);

    /// <summary>The applicative core: both valid → combined value; otherwise the errors of both, in order.</summary>
    private static Validated<TOut> Combine<TA, TB, TOut>(Validated<TA> first, Validated<TB> second, Func<TA, TB, TOut> combine) =>
        first.Match(
            a => second.Match(b => Validated<TOut>.Succeed(combine(a, b)), Validated<TOut>.Fail),
            errors => second.Match(_ => Validated<TOut>.Fail(errors), more => Validated<TOut>.Fail(errors.AddRange(more))));

    public static Validated<TResult> Apply<T1, TResult>(this Func<T1, TResult> map, Validated<T1> value) =>
        Valid(map).Apply(value);

    public static Validated<TResult> Apply<T1, TResult>(this Validated<Func<T1, TResult>> map, Validated<T1> value) =>
        Combine(map, value, (f, t1) => f(t1));

    public static Validated<Func<T2, TResult>> Apply<T1, T2, TResult>(this Func<T1, T2, TResult> map, Validated<T1> value) =>
        Valid(map).Apply(value);

    public static Validated<Func<T2, TResult>> Apply<T1, T2, TResult>(this Validated<Func<T1, T2, TResult>> map, Validated<T1> value) =>
        Combine(map, value, (f, t1) => (Func<T2, TResult>)((T2 t2) => f(t1, t2)));

    public static Validated<Func<T2, T3, TResult>> Apply<T1, T2, T3, TResult>(this Func<T1, T2, T3, TResult> map, Validated<T1> value) =>
        Valid(map).Apply(value);

    public static Validated<Func<T2, T3, TResult>> Apply<T1, T2, T3, TResult>(this Validated<Func<T1, T2, T3, TResult>> map, Validated<T1> value) =>
        Combine(map, value, (f, t1) => (Func<T2, T3, TResult>)((T2 t2, T3 t3) => f(t1, t2, t3)));

    public static Validated<Func<T2, T3, T4, TResult>> Apply<T1, T2, T3, T4, TResult>(this Func<T1, T2, T3, T4, TResult> map, Validated<T1> value) =>
        Valid(map).Apply(value);

    public static Validated<Func<T2, T3, T4, TResult>> Apply<T1, T2, T3, T4, TResult>(this Validated<Func<T1, T2, T3, T4, TResult>> map, Validated<T1> value) =>
        Combine(map, value, (f, t1) => (Func<T2, T3, T4, TResult>)((T2 t2, T3 t3, T4 t4) => f(t1, t2, t3, t4)));

    public static Validated<Func<T2, T3, T4, T5, TResult>> Apply<T1, T2, T3, T4, T5, TResult>(this Func<T1, T2, T3, T4, T5, TResult> map, Validated<T1> value) =>
        Valid(map).Apply(value);

    public static Validated<Func<T2, T3, T4, T5, TResult>> Apply<T1, T2, T3, T4, T5, TResult>(this Validated<Func<T1, T2, T3, T4, T5, TResult>> map, Validated<T1> value) =>
        Combine(map, value, (f, t1) => (Func<T2, T3, T4, T5, TResult>)((T2 t2, T3 t3, T4 t4, T5 t5) => f(t1, t2, t3, t4, t5)));

    public static Validated<Func<T2, T3, T4, T5, T6, TResult>> Apply<T1, T2, T3, T4, T5, T6, TResult>(this Func<T1, T2, T3, T4, T5, T6, TResult> map, Validated<T1> value) =>
        Valid(map).Apply(value);

    public static Validated<Func<T2, T3, T4, T5, T6, TResult>> Apply<T1, T2, T3, T4, T5, T6, TResult>(this Validated<Func<T1, T2, T3, T4, T5, T6, TResult>> map, Validated<T1> value) =>
        Combine(map, value, (f, t1) => (Func<T2, T3, T4, T5, T6, TResult>)((T2 t2, T3 t3, T4 t4, T5 t5, T6 t6) => f(t1, t2, t3, t4, t5, t6)));

    public static Validated<Func<T2, T3, T4, T5, T6, T7, TResult>> Apply<T1, T2, T3, T4, T5, T6, T7, TResult>(this Func<T1, T2, T3, T4, T5, T6, T7, TResult> map, Validated<T1> value) =>
        Valid(map).Apply(value);

    public static Validated<Func<T2, T3, T4, T5, T6, T7, TResult>> Apply<T1, T2, T3, T4, T5, T6, T7, TResult>(this Validated<Func<T1, T2, T3, T4, T5, T6, T7, TResult>> map, Validated<T1> value) =>
        Combine(map, value, (f, t1) => (Func<T2, T3, T4, T5, T6, T7, TResult>)((T2 t2, T3 t3, T4 t4, T5 t5, T6 t6, T7 t7) => f(t1, t2, t3, t4, t5, t6, t7)));

    public static Validated<Func<T2, T3, T4, T5, T6, T7, T8, TResult>> Apply<T1, T2, T3, T4, T5, T6, T7, T8, TResult>(this Func<T1, T2, T3, T4, T5, T6, T7, T8, TResult> map, Validated<T1> value) =>
        Valid(map).Apply(value);

    public static Validated<Func<T2, T3, T4, T5, T6, T7, T8, TResult>> Apply<T1, T2, T3, T4, T5, T6, T7, T8, TResult>(this Validated<Func<T1, T2, T3, T4, T5, T6, T7, T8, TResult>> map, Validated<T1> value) =>
        Combine(map, value, (f, t1) => (Func<T2, T3, T4, T5, T6, T7, T8, TResult>)((T2 t2, T3 t3, T4 t4, T5 t5, T6 t6, T7 t7, T8 t8) => f(t1, t2, t3, t4, t5, t6, t7, t8)));}
