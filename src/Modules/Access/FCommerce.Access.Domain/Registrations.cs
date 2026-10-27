using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Localization;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Persistence;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;

namespace FCommerce.Access.Domain;

public sealed record CompanyAddress(FormText Street, PostalCode PostalCode, FormText City, CountryCode Country);

/// <summary>The company as the applicant describes it (the customer itself always comes from the ERP).</summary>
public sealed record ApplicantCompany(FormText Name, TaxId TaxId, CompanyAddress Address);

public sealed record Applicant(PersonName Name, EmailAddress Email, Maybe<FormText> Phone, CultureCode Culture);

public abstract record RegistrationStatus
{
    private RegistrationStatus()
    {
    }

    public static RegistrationStatus Pending { get; } = new PendingDecision();

    public sealed record PendingDecision : RegistrationStatus;

    /// <param name="CustomerCode">The ERP customer the applicant was linked to.</param>
    public sealed record Approved(Guid DecidedBy, DateTimeOffset At, CustomerCode CustomerCode) : RegistrationStatus;

    public sealed record Rejected(Guid DecidedBy, DateTimeOffset At, FormText Reason) : RegistrationStatus;
}

/// <summary>A company's request for platform access, decided by an operator; made only by <see cref="Registrations.Submit"/> and decided once.</summary>
public sealed record RegistrationRequest : IPersistedValue<RegistrationRequest, RegistrationRequest.Stored>
{
    private RegistrationRequest(Stored stored) => Value = stored;

    private Stored Value { get; }

    public Guid Id => Value.Id;

    public ApplicantCompany Company => Value.Company;

    public Applicant Applicant => Value.Applicant;

    public DateTimeOffset SubmittedAt => Value.SubmittedAt;

    public RegistrationStatus Status => Value.Status;

    internal static RegistrationRequest Submit(Guid id, ApplicantCompany company, Applicant applicant, DateTimeOffset now) =>
        new(new Stored(id, company, applicant, now, RegistrationStatus.Pending));

    internal RegistrationRequest Decide(RegistrationStatus status) => new(Value with { Status = status });

    /// <param name="Status">Decided no earlier than submitted.</param>
    public sealed record Stored(Guid Id, ApplicantCompany Company, Applicant Applicant, DateTimeOffset SubmittedAt, RegistrationStatus Status);

    static bool IPersistedValue<RegistrationRequest, Stored>.IsWellFormed(Stored raw) =>
        raw.Status switch
        {
            RegistrationStatus.Approved approved => approved.At >= raw.SubmittedAt,
            RegistrationStatus.Rejected rejected => rejected.At >= raw.SubmittedAt,
            _ => true,
        };

    static RegistrationRequest IPersistedValue<RegistrationRequest, Stored>.Rehydrate(Stored raw) => new(raw);

    Stored IPersistedValue<RegistrationRequest, Stored>.Dehydrate() => Value;
}

/// <summary>The public form as submitted; missing required texts arrive as empty.</summary>
[RawInput]
public sealed record UnvalidatedRegistration(
    string CompanyName,
    string TaxId,
    string Street,
    string PostalCode,
    string City,
    string Country,
    string PersonName,
    string Email,
    Maybe<string> Phone,
    string Culture,
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
            .Apply(((Func<PersonName, EmailAddress, Maybe<FormText>, CultureCode, Applicant>)((name, email, phone, culture) => new Applicant(name, email, phone, culture)))
                .Apply(Domain.PersonName.Parse(input.PersonName).ToValidated("personName"))
                .Apply(EmailAddress.Parse(input.Email).ToValidated("email"))
                .Apply(Maybe.ParseOptional(input.Phone, phone => FormText.Parse(phone, MaxPhone, "phone")).ToValidated("phone"))
                .Apply(CultureCode.Parse(input.Culture).ToValidated("culture")))
            .Apply((input.AcceptedTerms
                ? Result.Ok<bool, Error>(true)
                : Error.Validation("registration.terms-required", "The terms must be accepted.")).ToValidated("acceptedTerms"));

    public static RegistrationRequest Submit(Guid id, ApplicantCompany company, Applicant applicant, DateTimeOffset now) =>
        RegistrationRequest.Submit(id, company, applicant, now);

    public static Result<RegistrationRequest, Error> Approve(RegistrationRequest request, Guid operatorId, CustomerCode customerCode, DateTimeOffset now) =>
        Pending(request).Map(pending => pending.Decide(new RegistrationStatus.Approved(operatorId, now, customerCode)));

    /// <summary>The rejected request with the reason the applicant is told.</summary>
    public static Result<(RegistrationRequest Request, FormText Reason), Error> Reject(RegistrationRequest request, Guid operatorId, string reason, DateTimeOffset now) =>
        from pending in Pending(request)
        from text in FormText.Parse(reason, MaxReason, "reason", allowControl: true).MapError(e => e with { Field = "reason" })
        select (pending.Decide(new RegistrationStatus.Rejected(operatorId, now, text)), text);

    private static Result<RegistrationRequest, Error> Pending(RegistrationRequest request) =>
        request.Status is RegistrationStatus.PendingDecision
            ? request
            : Error.Conflict("registration.already-decided", "This request has already been decided.");

    private static Validated<ApplicantCompany> ValidateCompany(UnvalidatedRegistration input) =>
        CountryCode.Parse(input.Country).ToValidated("country").Match(
            country => ((Func<FormText, TaxId, FormText, PostalCode, FormText, ApplicantCompany>)((name, taxId, street, postal, city) =>
                    new ApplicantCompany(name, taxId, new CompanyAddress(street, postal, city, country))))
                .Apply(FormText.Parse(input.CompanyName, MaxCompanyName, "company-name").ToValidated("companyName"))
                .Apply(TaxId.Parse(input.TaxId).ToValidated("taxId"))
                .Apply(FormText.Parse(input.Street, MaxStreet, "street").ToValidated("street"))
                .Apply(PostalCode.Parse(input.PostalCode, country).ToValidated("postalCode"))
                .Apply(FormText.Parse(input.City, MaxCity, "city").ToValidated("city")),
            errors => Validated.Invalid<ApplicantCompany>(errors[0], [.. errors.Skip(1)]));
}
