using System.Globalization;
using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Cart.Domain;

/// <summary>A pasted line that could not be read.</summary>
/// <param name="Line">Its number in the pasted text, from 1.</param>
public sealed record PastedLineError(int Line, string Text, Error Error);

public sealed record PastedItems(EquatableArray<CartItem> Items, EquatableArray<PastedLineError> Errors);

/// <summary>Reading lines pasted by a buyer, e.g. copied from a spreadsheet: "SKU quantity" per line.</summary>
public static class QuickOrder
{
    public const int MaxLines = 200;

    /// <summary>
    /// Every non-empty line is a SKU and an optional quantity (1 when missing), separated by a tab, a semicolon,
    /// spaces or a comma; a decimal comma is accepted ("SR-M8 2,5"). The same SKU on several lines is summed.
    /// </summary>
    public static PastedItems Parse(string text)
    {
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n')
            .Select((line, index) => (Number: index + 1, Text: line.Trim()))
            .Where(line => line.Text.Length > 0)
            .ToList();
        if (lines.Count > MaxLines)
        {
            return new PastedItems([], [new PastedLineError(lines[MaxLines].Number, lines[MaxLines].Text, Error.Validation("paste.too-many-lines", $"At most {MaxLines} lines at once."))]);
        }

        var read = lines.Select(line => (line.Number, line.Text, Item: Line(line.Text))).ToList();
        var summed = read.Choose(r => r.Item.ToMaybe().Map(item => (r.Number, r.Text, Item: item)))
            .GroupBy(r => r.Item.Sku)
            .Select(group => (Last: group.Last(), Sum: Carts.Normalize(group.Select(r => r.Item)).Map(items => items[0])))
            .ToList();
        return new PastedItems(
            summed.Choose(s => s.Sum.ToMaybe()).ToEquatableArray(),
            [
                .. read.Choose(r => r.Item.Match(_ => Maybe<PastedLineError>.None, error => new PastedLineError(r.Number, r.Text, error))),
                .. summed.Choose(s => s.Sum.Match(_ => Maybe<PastedLineError>.None, error => new PastedLineError(s.Last.Number, s.Last.Text, error))),
            ]);
    }

    private static Result<CartItem, Error> Line(string text)
    {
        var parts = Split(text);
        return parts.Length switch
        {
            1 => Carts.Sku(parts[0]).Map(sku => new CartItem(sku, CartQuantity.One)),
            2 => from sku in Carts.Sku(parts[0])
                 from quantity in Number(parts[1]).Bind(CartQuantity.Parse)
                 select new CartItem(sku, quantity),
            _ => Error.Validation("paste.format", "Expected a SKU and a quantity."),
        };
    }

    /// <summary>Tab or semicolon when present; otherwise spaces; otherwise a comma.</summary>
    private static string[] Split(string text)
    {
        var options = StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries;
        return text.IndexOfAny(['\t', ';']) >= 0 ? text.Split(['\t', ';'], options)
            : text.IndexOfAny([' ', '\u00a0']) >= 0 ? text.Split([' ', '\u00a0'], options)
            : text.Split(',', options);
    }

    private static Result<decimal, Error> Number(string text) =>
        decimal.TryParse(text.Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            ? number
            : Error.Validation("paste.quantity", $"'{text}' is not a quantity.");
}
