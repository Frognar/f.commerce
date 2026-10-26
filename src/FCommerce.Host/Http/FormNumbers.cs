using System.Globalization;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Host.Http;

/// <summary>Numbers typed into forms as text, so that a decimal comma and a point both work whatever the page culture.</summary>
internal static class FormNumbers
{
    /// <summary>A number with a decimal comma or point; its range is checked by the module that uses it.</summary>
    public static Result<decimal, Error> Decimal(string raw, string errorCode, string field) =>
        decimal.TryParse(
            raw.Trim().Replace(',', '.'),
            NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out var number)
            ? number
            : Error.Validation(errorCode, "Enter a number.") with { Field = field };

    /// <summary>A whole number (digits only).</summary>
    public static Result<int, Error> Whole(string raw, string errorCode, string field) =>
        int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : Error.Validation(errorCode, "Enter a whole number.") with { Field = field };
}
