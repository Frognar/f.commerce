using FCommerce.SharedKernel.Messaging;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Access.Contracts;

/// <summary>A company applies for platform access (feature "self-registration"); operators decide.</summary>
/// <param name="TaxId">Required: it is how operators match the applicant to an ERP customer.</param>
public sealed record SubmitRegistration(
    string CompanyName,
    string TaxId,
    string Street,
    string PostalCode,
    string City,
    string Country,
    string PersonName,
    string Email,
    string? Phone,
    string Culture,
    bool AcceptedTerms) : ICommand<Result<Guid, Error>>;

/// <param name="Status">"pending", "approved", "rejected"; null for all.</param>
public sealed record GetRegistrationRequests(string? Status = "pending") : IQuery<IReadOnlyList<RegistrationSummary>>;

public sealed record GetRegistrationRequest(Guid Id) : IQuery<Result<RegistrationDetails, Error>>;

public sealed record RegistrationSummary(Guid Id, string CompanyName, string TaxId, string PersonName, string Email, DateTimeOffset SubmittedAt, string Status);

/// <param name="Status">"pending", "approved" or "rejected".</param>
/// <param name="CustomerCode">The customer the applicant was linked to (approved requests).</param>
public sealed record RegistrationDetails(
    Guid Id,
    string CompanyName,
    string TaxId,
    string Street,
    string PostalCode,
    string City,
    string Country,
    string PersonName,
    string Email,
    string? Phone,
    string Culture,
    DateTimeOffset SubmittedAt,
    string Status,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? CustomerCode,
    string? RejectionReason);

/// <summary>
/// Links the applicant to an ERP customer with the template's rights: a new person gets an invitation, an existing
/// account the access.
/// </summary>
public sealed record ApproveRegistration(Guid ActorId, Guid RequestId, string CustomerCode, string Template) : ICommand<Result<Unit, Error>>;

/// <summary>Rejects the request; the applicant gets the reason by e-mail.</summary>
public sealed record RejectRegistration(Guid ActorId, Guid RequestId, string Reason) : ICommand<Result<Unit, Error>>;
