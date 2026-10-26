using System.Collections;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Persistence;

namespace FCommerce.SharedKernel.Collections;

/// <summary>
/// An immutable array with at least one element, compared by value. Created only through <see cref="NonEmptyArray.Create{T}"/>
/// (none for an empty sequence) or <see cref="NonEmptyArray.Of{T}"/>, so <see cref="Head"/> always exists.
/// </summary>
public sealed record NonEmptyArray<T> : IReadOnlyList<T>, IPersistedValue<NonEmptyArray<T>, EquatableArray<T>>
    where T : notnull
{
    internal NonEmptyArray(EquatableArray<T> items) => Items = items;

    public EquatableArray<T> Items { get; }

    public T Head => Items[0];

    public int Count => Items.Count;

    public T this[int index] => Items[index];

    public NonEmptyArray<TOut> Map<TOut>(Func<T, TOut> map)
        where TOut : notnull =>
        new(Items.Select(map).ToEquatableArray());

    public NonEmptyArray<T> Add(T item) => new(Items.Add(item));

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    static NonEmptyArray<T> IPersistedValue<NonEmptyArray<T>, EquatableArray<T>>.Rehydrate(EquatableArray<T> raw) => new(raw);

    static bool IPersistedValue<NonEmptyArray<T>, EquatableArray<T>>.IsWellFormed(EquatableArray<T> raw) => !raw.IsEmpty;

    EquatableArray<T> IPersistedValue<NonEmptyArray<T>, EquatableArray<T>>.Dehydrate() => Items;

    public override string ToString() => Items.ToString();
}

public static class NonEmptyArray
{
    public static NonEmptyArray<T> Of<T>(T head, params ReadOnlySpan<T> tail)
        where T : notnull =>
        new([head, .. tail]);

    /// <summary>The items as a non-empty array; none when there are none.</summary>
    public static Maybe<NonEmptyArray<T>> Create<T>(IEnumerable<T> items)
        where T : notnull
    {
        var array = items.ToEquatableArray();
        return array.IsEmpty ? Maybe<NonEmptyArray<T>>.None : new NonEmptyArray<T>(array);
    }

    public static Maybe<NonEmptyArray<T>> ToNonEmptyArray<T>(this IEnumerable<T> items)
        where T : notnull =>
        Create(items);
}
