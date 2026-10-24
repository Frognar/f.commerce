using FCommerce.Application.EventStore;
using FCommerce.Testing;

namespace FCommerce.Application.Tests;

public sealed class StreamIdTests
{
    private sealed class CartStream : IStreamType
    {
        public static string Category => "cart";
    }

    [Fact]
    public void Builds_stream_ids_from_a_stream_type()
    {
        var id = Guid.Parse("0193a2b4-0000-7000-8000-000000000001");

        var stream = StreamId.For<CartStream>(id);

        Assert.Equal("cart-0193a2b4000070008000000000000001", stream.Value);
        Assert.Equal(stream, StreamId.Parse(stream.Value).ShouldSucceed());
    }

    [Theory]
    [InlineData("order-42", "order", "42")]
    [InlineData("cart-a-b", "cart", "a-b")]
    [InlineData("v2order-1", "v2order", "1")]
    public void Parses_keys_at_the_first_separator(string key, string category, string id)
    {
        var stream = StreamId.Parse(key).ShouldSucceed();

        Assert.Equal((category, id), (stream.Category, stream.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("cart")]
    [InlineData("cart-")]
    [InlineData("-42")]
    [InlineData("Cart-42")]
    [InlineData("2cart-42")]
    [InlineData("$foreign-42")]
    public void Rejects_malformed_keys(string key)
    {
        Assert.StartsWith("stream-id.", StreamId.Parse(key).ShouldFail().Code, StringComparison.Ordinal);
    }

    [Fact]
    public void Foreign_keys_are_kept_under_a_category_no_stream_type_can_use()
    {
        var stream = StreamId.Foreign("mt_tombstone");

        Assert.Equal(StreamId.ForeignCategory, stream.Category);
        StreamId.ValidateCategory(StreamId.ForeignCategory).ShouldFail();
    }
}
