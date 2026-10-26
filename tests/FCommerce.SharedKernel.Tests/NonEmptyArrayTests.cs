using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Optional;
using FCommerce.Testing;

namespace FCommerce.SharedKernel.Tests;

public sealed class NonEmptyArrayTests
{
    [Fact]
    public void An_empty_sequence_is_no_array()
    {
        Assert.Equal(Maybe<NonEmptyArray<int>>.None, NonEmptyArray.Create<int>([]));
    }

    [Fact]
    public void Keeps_the_items_in_order_with_the_first_as_head()
    {
        var array = NonEmptyArray.Create([3, 1, 2]).ShouldBeSome();

        Assert.Equal(3, array.Head);
        Assert.Equal([3, 1, 2], array);
    }

    [Fact]
    public void Equality_is_by_value()
    {
        Assert.Equal(NonEmptyArray.Of("a", "b"), NonEmptyArray.Create(["a", "b"]).ShouldBeSome());
        Assert.NotEqual(NonEmptyArray.Of("a", "b"), NonEmptyArray.Of("b", "a"));
    }

    [Fact]
    public void Map_keeps_it_non_empty()
    {
        Assert.Equal(NonEmptyArray.Of(2, 4), NonEmptyArray.Of(1, 2).Map(n => n * 2));
    }
}
