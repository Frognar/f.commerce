using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;

namespace FCommerce.Access.Domain;

public sealed record CompanyAddress(string Street, PostalCode PostalCode, string City, CountryCode Country);

/// <summary>The company as the applicant describes it (the customer itself always comes from the ERP).</summary>
public sealed record ApplicantCompany(string Name, TaxId TaxId, CompanyAddress Address);

public sealed record Applicant(PersonName Name, EmailAddress Email, string? Phone, CultureCode Culture);

public abstract record RegistrationStatus
{
    private RegistrationStatus()
    {
    }

    public static RegistrationStatus Pending { get; } = new PendingDecision();

    public sealed record PendingDecision : RegistrationStatus;

    /// <param name="CustomerCode">The ERP customer the applicant was linked to.</param>
    public sealed record Approved(Guid DecidedBy, DateTimeOffset At, string CustomerCode) : RegistrationStatus;

    public sealed record Rejected(Guid DecidedBy, DateTimeOffset At, string Reason) : RegistrationStatus;
}

/// <summary>A company's request for platform access, decided by an operator.</summary>
public sealed record RegistrationRequest(Guid Id, ApplicantCompany Company, Applicant Applicant, DateTimeOffset SubmittedAt, RegistrationStatus Status);

public sealed record UnvalidatedRegistration(
    string? CompanyName,
    string? TaxId,
    string? Street,
    string? PostalCode,
    string? City,
    string? Country,
    string? PersonName,
    string? Email,
    string? Phone,
    string? Culture,
    bool AcceptedTerms);

public static class Registrations
{
    public const int MaxCompanyName = 300;
    public const int MaxStreet = 200;
    public const int MaxCity = 100;
    public const int MaxPhone = 40;
    public const int MaxReason = 1000;

    private static readonly Func<ApplicantCompany, Applicant, Func<bool, (ApplicantCompany Company, Applicant Applicant)>> Create =
        (company, applicant) => _ => (company, applicant);

    /// <summary>Validates the public form; every problem is reported with its field.</summary>
    public static Validated<(ApplicantCompany Company, Applicant Applicant)> Validate(UnvalidatedRegistration input) =>
        Create
            .Apply(ValidateCompany(input))
            .Apply(((Func<PersonName, EmailAddress, string?, CultureCode, Applicant>)((name, email, phone, culture) => new Applicant(name, email, phone, culture)))
                .Apply(Domain.PersonName.Parse(input.PersonName).ToValidated("personName"))
                .Apply(EmailAddress.Parse(input.Email).ToValidated("email"))
                .Apply(Text(input.Phone, MaxPhone, "phone", optional: true).ToValidated("phone"))
                .Apply(CultureCode.Parse(input.Culture).ToValidated("culture")))
            .Apply((input.AcceptedTerms
                ? Result.Ok<bool, Error>(true)
                : Error.Validation("registration.terms-required", "The terms must be accepted.")).ToValidated("acceptedTerms"));

    public static RegistrationRequest Submit(Guid id, ApplicantCompany company, Applicant applicant, DateTimeOffset now) =>
        new(id, company, applicant, now, RegistrationStatus.Pending);

    public static Result<RegistrationRequest, Error> Approve(RegistrationRequest request, Guid operatorId, string customerCode, DateTimeOffset now) =>
        Pending(request).Map(pending => pending with { Status = new RegistrationStatus.Approved(operatorId, now, customerCode) });

    public static Result<RegistrationRequest, Error> Reject(RegistrationRequest request, Guid operatorId, string? reason, DateTimeOffset now) =>
        from pending in Pending(request)
        from text in Text(reason, MaxReason, "reason", optional: false).MapError(e => e with { Field = "reason" })
        select pending with { Status = new RegistrationStatus.Rejected(operatorId, now, text!) };

    private static Result<RegistrationRequest, Error> Pending(RegistrationRequest request) =>
        request.Status is RegistrationStatus.PendingDecision
            ? request
            : Error.Conflict("registration.already-decided", "This request has already been decided.");

    private static Validated<ApplicantCompany> ValidateCompany(UnvalidatedRegistration input) =>
        CountryCode.Parse(input.Country).ToValidated("country").Match(
            country => ((Func<string, TaxId, string, PostalCode, string, ApplicantCompany>)((name, taxId, street, postal, city) =>
                    new ApplicantCompany(name, taxId, new CompanyAddress(street, postal, city, country))))
                .Apply(Text(input.CompanyName, MaxCompanyName, "company-name", optional: false).Map(t => t!).ToValidated("companyName"))
                .Apply(TaxId.Parse(input.TaxId).ToValidated("taxId"))
                .Apply(Text(input.Street, MaxStreet, "street", optional: false).Map(t => t!).ToValidated("street"))
                .Apply(PostalCode.Parse(input.PostalCode, country).ToValidated("postalCode"))
                .Apply(Text(input.City, MaxCity, "city", optional: false).Map(t => t!).ToValidated("city")),
            errors => Validated.Invalid<ApplicantCompany>(errors[0], [.. errors.Skip(1)]));

    private static Result<string?, Error> Text(string? raw, int maxLength, string code, bool optional)
    {
        var value = raw?.Trim();
        return value switch
        {
            null or "" when optional => Result.Ok<string?, Error>(null),
            null or "" => Error.Validation($"{code}.empty", "This field is required."),
            { Length: var length } when length > maxLength => Error.Validation($"{code}.too-long", $"At most {maxLength} characters."),
            _ when value.Any(char.IsControl) && code != "reason" => Error.Validation($"{code}.invalid", "Control characters are not allowed."),
            _ => value,
        };
    }
}
