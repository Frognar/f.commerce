using System.Collections;
using System.Collections.Immutable;

namespace FCommerce.SharedKernel.Collections;

/// <summary>
/// An immutable array with structural equality, so records that contain collections
/// compare by value (ImmutableArray alone compares by reference).
/// </summary>
public readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
{
    private readonly ImmutableArray<T> items;

    public EquatableArray(ImmutableArray<T> items) => this.items = items;

    public static EquatableArray<T> Empty => new(ImmutableArray<T>.Empty);

    public ImmutableArray<T> AsImmutableArray() => items.IsDefault ? ImmutableArray<T>.Empty : items;

    public int Count => AsImmutableArray().Length;

    public bool IsEmpty => Count == 0;

    public T this[int index] => AsImmutableArray()[index];

    public EquatableArray<T> Add(T item) => new(AsImmutableArray().Add(item));

    public EquatableArray<T> AddRange(IEnumerable<T> range) => new(AsImmutableArray().AddRange(range));

    public EquatableArray<T> RemoveAll(Predicate<T> match) => new(AsImmutableArray().RemoveAll(match));

    public EquatableArray<T> SetItem(int index, T item) => new(AsImmutableArray().SetItem(index, item));

    public bool Equals(EquatableArray<T> other) => AsImmutableArray().AsSpan().SequenceEqual(other.AsImmutableArray().AsSpan());

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in AsImmutableArray())
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);

    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);

    public static implicit operator EquatableArray<T>(ImmutableArray<T> items) => new(items);

    public ImmutableArray<T>.Enumerator GetEnumerator() => AsImmutableArray().GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)AsImmutableArray()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)AsImmutableArray()).GetEnumerator();

    public override string ToString() => $"[{string.Join(", ", AsImmutableArray())}]";
}

public static class EquatableArray
{
    public static EquatableArray<T> Create<T>(params ReadOnlySpan<T> items) => new([.. items]);

    public static EquatableArray<T> ToEquatableArray<T>(this IEnumerable<T> items) => new([.. items]);
}
