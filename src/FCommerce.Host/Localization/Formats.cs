using System.Globalization;

namespace FCommerce.Host.Localization;

/// <summary>Numbers shown to people, in the culture of the request.</summary>
public static class Formats
{
    /// <summary>An amount with at least two decimal places and up to four (cheap items are priced in fractions of a grosz).</summary>
    public static string Amount(decimal amount) => amount.ToString("#,##0.00##", CultureInfo.CurrentCulture);

    public static string Money(decimal amount, string currency) => $"{Amount(amount)} {currency}";

    /// <summary>A quantity with up to three decimal places (kilograms, metres).</summary>
    public static string Quantity(decimal quantity) => quantity.ToString("#,##0.###", CultureInfo.CurrentCulture);

    public static string Percent(decimal percent) => percent.ToString("0.##", CultureInfo.CurrentCulture) + "%";

    public static string Date(DateOnly date) => date.ToString("d", CultureInfo.CurrentCulture);
}
