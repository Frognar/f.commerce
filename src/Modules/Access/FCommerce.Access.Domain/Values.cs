using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Access.Domain;

/// <summary>The ERP code of a customer a user acts for.</summary>
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

/// <summary>The ERP's id of a customer's contact person.</summary>
public sealed record ErpContactId : IPersistedValue<ErpContactId, string>
{
    public const int MaxLength = 64;

    private ErpContactId(string value) => Value = value;

    public string Value { get; }

    public static Result<ErpContactId, Error> Parse(string raw) =>
        SourceCodes.Parse(raw, MaxLength, "contact-id", "Contact id").Map(value => new ErpContactId(value));

    static ErpContactId IPersistedValue<ErpContactId, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<ErpContactId, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>A password that passed <see cref="PasswordPolicy"/>: the only input the shell hashes.</summary>
public sealed record ValidPassword
{
    internal ValidPassword(string value) => Value = value;

    public string Value { get; }

    public override string ToString() => "***";
}

/// <summary>The stored hash of a password (the shell's hashing format, opaque to the domain).</summary>
public sealed record PasswordHash : IPersistedValue<PasswordHash, string>
{
    private PasswordHash(string value) => Value = value;

    public string Value { get; }

    public static Result<PasswordHash, Error> Parse(string raw) =>
        raw.Length > 0 && !raw.Any(char.IsWhiteSpace)
            ? new PasswordHash(raw)
            : Error.Validation("password-hash.invalid", "A password hash is a non-empty text without spaces.");

    static PasswordHash IPersistedValue<PasswordHash, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<PasswordHash, string>.Dehydrate() => Value;

    public override string ToString() => "***";
}

/// <summary>Changes whenever credentials or access change; sessions and API keys carrying another stamp end.</summary>
public sealed record SecurityStamp : IPersistedValue<SecurityStamp, string>
{
    public const int MaxLength = 64;

    private SecurityStamp(string value) => Value = value;

    public string Value { get; }

    public static Result<SecurityStamp, Error> Parse(string raw) =>
        raw is { Length: > 0 and <= MaxLength } && raw.All(char.IsAsciiLetterOrDigit)
            ? new SecurityStamp(raw)
            : Error.Validation("security-stamp.invalid", $"A security stamp has 1–{MaxLength} letters or digits.");

    static SecurityStamp IPersistedValue<SecurityStamp, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<SecurityStamp, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>The stored hash of a one-time link token or of an API key's secret (lower-case hex SHA-256).</summary>
public sealed record SecretHash : IPersistedValue<SecretHash, string>
{
    public const int Length = 64;

    private SecretHash(string value) => Value = value;

    public string Value { get; }

    public static Result<SecretHash, Error> Parse(string raw) =>
        raw.Length == Length && raw.All(char.IsAsciiHexDigitLower)
            ? new SecretHash(raw)
            : Error.Validation("secret-hash.invalid", $"A secret hash has {Length} lower-case hex digits.");

    static SecretHash IPersistedValue<SecretHash, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<SecretHash, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>The name a user gives an API key: trimmed, 1–100 characters, no control characters.</summary>
public sealed record ApiKeyName : IPersistedValue<ApiKeyName, string>
{
    public const int MaxLength = 100;

    private ApiKeyName(string value) => Value = value;

    public string Value { get; }

    public static Result<ApiKeyName, Error> Parse(string raw)
    {
        var value = raw.Trim();
        return value switch
        {
            "" => Error.Validation("api-key.name-empty", "Name the key.") with { Field = "name" },
            { Length: > MaxLength } => Error.Validation("api-key.name-too-long", $"The name must not exceed {MaxLength} characters.") with { Field = "name" },
            _ when value.Any(char.IsControl) => Error.Validation("api-key.name-invalid", "The name must not contain control characters.") with { Field = "name" },
            _ => new ApiKeyName(value),
        };
    }

    static ApiKeyName IPersistedValue<ApiKeyName, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<ApiKeyName, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>The public identifier of an API key: lower-case hexadecimal characters.</summary>
public sealed record ApiKeyId : IPersistedValue<ApiKeyId, string>
{
    public const int Length = 12;

    private ApiKeyId(string value) => Value = value;

    public string Value { get; }

    public static Result<ApiKeyId, Error> Parse(string raw) =>
        raw.Length == Length && raw.All(char.IsAsciiHexDigitLower)
            ? new ApiKeyId(raw)
            : Error.Validation("api-key.invalid", "The API key is not valid.");

    static ApiKeyId IPersistedValue<ApiKeyId, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<ApiKeyId, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}

/// <summary>A required text of the registration form: trimmed, bounded; control characters only where allowed (free-form reasons).</summary>
public sealed record FormText : IPersistedValue<FormText, string>
{
    private FormText(string value) => Value = value;

    public string Value { get; }

    public static Result<FormText, Error> Parse(string raw, int maxLength, string code, bool allowControl = false)
    {
        var value = raw.Trim();
        return value switch
        {
            "" => Error.Validation($"{code}.empty", "This field is required."),
            { Length: var length } when length > maxLength => Error.Validation($"{code}.too-long", $"At most {maxLength} characters."),
            _ when !allowControl && value.Any(char.IsControl) => Error.Validation($"{code}.invalid", "Control characters are not allowed."),
            _ => new FormText(value),
        };
    }

    static FormText IPersistedValue<FormText, string>.Rehydrate(string raw) => new(raw);

    string IPersistedValue<FormText, string>.Dehydrate() => Value;

    public override string ToString() => Value;
}
