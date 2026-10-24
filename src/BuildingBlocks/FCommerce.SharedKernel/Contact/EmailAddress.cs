using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;

namespace FCommerce.SharedKernel.Contact;

/// <summary>
/// An e-mail address, trimmed and lowercased (addresses are compared and stored case-insensitively; mailbox
/// providers treat the local part case-insensitively in practice). Structural check only — deliverability is
/// proven by the invitation/reset e-mails.
/// </summary>
public sealed record EmailAddress : IPersistedValue<EmailAddress, string>
{
    public const int MaxLength = 254;

    private EmailAddress(string value) => Value = value;

    public string Value { get; }

    public string Domain => Value[(Value.LastIndexOf('@') + 1)..];

    public static Result<EmailAddress, Error> Parse(string raw)
    {
        var value = raw.Trim().ToLowerInvariant();
        var at = value.LastIndexOf('@');
        var valid = value.Length is > 2 and <= MaxLength
            && at is > 0 and <= 64
            && value.IndexOf('@') == at
            && value[(at + 1)..] is var domain
            && domain.Contains('.', StringComparison.Ordinal)
            && !domain.StartsWith('.') && !domain.EndsWith('.') && !domain.Contains("..", StringComparison.Ordinal)
            && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '<' or '>' or '(' or ')' or ',' or ';' or ':' or '\\' or '"' or '[' or ']');

        return valid
            ? new EmailAddress(value)
            : Error.Validation("email.invalid", $"'{raw}' is not a valid e-mail address.");
    }

    static EmailAddress IPersistedValue<EmailAddress, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<EmailAddress, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}
