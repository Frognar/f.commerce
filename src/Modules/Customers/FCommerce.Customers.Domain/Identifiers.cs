using System.Text;
using FCommerce.Authorization.Core;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Customers.Domain;

/// <summary>Customer identifier from the ERP.</summary>
public sealed record CustomerCode : IPersistedValue<CustomerCode, string>
{
    public const int MaxLength = 64;

    private CustomerCode(string value) => Value = value;

    public string Value { get; }

    public static Result<CustomerCode, Error> Parse(string raw) =>
        SourceCodes.Parse(raw, MaxLength, "customer-code", "Customer code").Map(value => new CustomerCode(value));

    static CustomerCode IPersistedValue<CustomerCode, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<CustomerCode, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>A short text of a customer from the ERP (codes, names, address parts, phone numbers): trimmed, bounded, no control characters.</summary>
public sealed record CustomerText : IPersistedValue<CustomerText, string>
{
    private CustomerText(string value) => Value = value;

    public string Value { get; }

    public static Result<CustomerText, Error> Parse(string raw, int maxLength, string errorPrefix, string label) =>
        SourceCodes.Parse(raw, maxLength, errorPrefix, label).Map(value => new CustomerText(value));

    /// <summary>Like <see cref="Parse"/>, with every run of whitespace made one space (names, streets, cities).</summary>
    public static Result<CustomerText, Error> Name(string raw, int maxLength, string errorPrefix, string label) =>
        SourceCodes.Parse(raw, maxLength, errorPrefix, label).Map(value => new CustomerText(Texts.Normalize(value)));

    public static Result<Maybe<CustomerText>, Error> ParseOptional(Maybe<string> raw, int maxLength, string errorPrefix, string label) =>
        SourceCodes.ParseOptional(raw, maxLength, errorPrefix, label).Map(value => value.Map(text => new CustomerText(text)));

    static CustomerText IPersistedValue<CustomerText, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<CustomerText, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>The rights template a contact with platform access is invited with: one the platform knows.</summary>
public sealed record ContactTemplate : IPersistedValue<ContactTemplate, string>
{
    private ContactTemplate(string value) => Value = value;

    public string Value { get; }

    public static Result<ContactTemplate, Error> Parse(string raw) =>
        MembershipAttributes.Templates.ContainsKey(raw)
            ? new ContactTemplate(raw)
            : Error.Validation("contact.template-unknown", $"Unknown attribute template '{raw}'.");

    static ContactTemplate IPersistedValue<ContactTemplate, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<ContactTemplate, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>Days the customer has to pay an invoice: 0–365.</summary>
public sealed record PaymentTerms : IPersistedValue<PaymentTerms, int>
{
    public const int MaxDays = 365;

    private PaymentTerms(int days) => Days = days;

    public int Days { get; }

    public static Result<PaymentTerms, Error> Parse(int days) =>
        days is >= 0 and <= MaxDays ? new PaymentTerms(days) : Error.Validation("payment-terms.invalid", $"Payment terms must be between 0 and {MaxDays} days.");

    static bool IPersistedValue<PaymentTerms, int>.IsWellFormed(int raw) => raw >= 0;

    static PaymentTerms IPersistedValue<PaymentTerms, int>.Rehydrate(int raw) => new(raw);

    int IPersistedValue<PaymentTerms, int>.Dehydrate() => Days;

    public override string ToString() => Days.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal static class Texts
{
    /// <summary>Trimmed, with every run of whitespace replaced by one space.</summary>
    public static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
        {
            if (!char.IsWhiteSpace(c))
            {
                builder.Append(c);
            }
            else if (builder[^1] != ' ')
            {
                builder.Append(' ');
            }
        }

        return builder.ToString();
    }
}
