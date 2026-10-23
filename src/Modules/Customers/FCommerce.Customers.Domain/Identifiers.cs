using System.Text;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Customers.Domain;

/// <summary>Customer identifier from the ERP.</summary>
public sealed record CustomerCode : IPersistedValue<CustomerCode, string>
{
    public const int MaxLength = 64;

    private CustomerCode(string value) => Value = value;

    public string Value { get; }

    public static Result<CustomerCode, Error> Parse(string? raw) =>
        SourceCodes.Parse(raw, MaxLength, "customer-code", "Customer code").Map(value => new CustomerCode(value));

    static CustomerCode IPersistedValue<CustomerCode, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<CustomerCode, string>.Dehydrate() => Value;

    public override string ToString() => Value;
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
