using FCommerce.Access.Contracts;
using FCommerce.Access.Domain;
using FCommerce.Access.Persistence;
using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Authorization.Core;
using FCommerce.Customers.Contracts;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Access.Handlers;

internal sealed class SubmitRegistrationHandler(AccessStore store) : ICommandHandler<SubmitRegistration, Result<Guid, Error>>
{
    public async Task<Result<Guid, Error>> HandleAsync(SubmitRegistration command, CancellationToken cancellationToken = default) =>
        await Registrations.Validate(new UnvalidatedRegistration(
                command.CompanyName, command.TaxId, command.Street, command.PostalCode, command.City, command.Country,
                command.PersonName, command.Email, command.Phone, command.Culture, command.AcceptedTerms))
            .ToResult()
            .BindAsync(async form =>
            {
                var request = Registrations.Submit(Guid.CreateVersion7(), form.Company, form.Applicant, store.Now);
                store.Db.Registrations.Add(RegistrationRow.From(request));
                return await store.Enqueue(new RegistrationSubmitted(request.Id, request.Company.Name, request.Company.TaxId.Value, request.Applicant.Name.Value, request.Applicant.Email.Value))
                    .MapAsync(async _ =>
                    {
                        store.Audit(null, "registration.submitted", $"registration:{request.Id}", new { email = request.Applicant.Email.Value, taxId = request.Company.TaxId.Value });
                        await store.Db.SaveChangesAsync(cancellationToken);
                        return request.Id;
                    });
            });
}

internal sealed class ApproveRegistrationHandler(
    AccessStore store,
    MembershipManagement memberships,
    IQueryHandler<GetCustomer, Result<CustomerDetails, Error>> customers)
    : ICommandHandler<ApproveRegistration, Result<Unit, Error>>
{
    public async Task<Result<Unit, Error>> HandleAsync(ApproveRegistration command, CancellationToken cancellationToken = default)
    {
        var row = await store.Db.Registrations.SingleOrDefaultAsync(r => r.Id == command.RequestId, cancellationToken);
        var actor = (await store.FindAsync(command.ActorId, cancellationToken))?.ToDomain();
        if (row is null)
        {
            return Error.NotFound("registration.not-found", "The request does not exist.");
        }

        if (actor is null || AccessStore.AsOperator(actor) is not { Attributes.CanDecideRegistrations: true })
        {
            return Error.Forbidden("grant.not-allowed", "Only operators who decide registrations can approve them.");
        }

        if (!MembershipAttributes.Templates.TryGetValue(command.Template, out var template))
        {
            return Error.Validation("registration.template-unknown", "Choose the rights for the applicant.") with { Field = "template" };
        }

        var request = row.ToDomain();
        return await (await customers.HandleAsync(new GetCustomer(command.CustomerCode), cancellationToken)).BindAsync(async customer =>
            await Registrations.Approve(request, command.ActorId, customer.Code, store.Now).BindAsync(async approved =>
            {
                var invitee = new CustomerInvitee(request.Applicant.Email, request.Applicant.Name, request.Applicant.Culture, MembershipRights.Of(template));
                return await (await memberships.StageAsync(command.ActorId, customer, invitee, MembershipSource.Registration, cancellationToken)).MapAsync(async _ =>
                {
                    row.Apply(approved);
                    store.Audit(command.ActorId, "registration.approved", $"registration:{request.Id}", new { customer = customer.Code });
                    await store.Db.SaveChangesAsync(cancellationToken);
                    return Unit.Value;
                });
            }));
    }
}

internal sealed class RejectRegistrationHandler(AccessStore store) : ICommandHandler<RejectRegistration, Result<Unit, Error>>
{
    public async Task<Result<Unit, Error>> HandleAsync(RejectRegistration command, CancellationToken cancellationToken = default)
    {
        var row = await store.Db.Registrations.SingleOrDefaultAsync(r => r.Id == command.RequestId, cancellationToken);
        var actor = (await store.FindAsync(command.ActorId, cancellationToken))?.ToDomain();
        if (row is null)
        {
            return Error.NotFound("registration.not-found", "The request does not exist.");
        }

        if (actor is null || AccessStore.AsOperator(actor) is not { Attributes.CanDecideRegistrations: true })
        {
            return Error.Forbidden("grant.not-allowed", "Only operators who decide registrations can reject them.");
        }

        var request = row.ToDomain();
        return await Registrations.Reject(request, actor.Id, command.Reason, store.Now).BindAsync(async rejected =>
        {
            var reason = ((RegistrationStatus.Rejected)rejected.Status).Reason;
            row.Apply(rejected);
            return await store.Enqueue(new RegistrationRejected(request.Id, request.Applicant.Email.Value, request.Applicant.Name.Value, request.Applicant.Culture.Value, request.Company.Name, reason))
                .MapAsync(async _ =>
                {
                    store.Audit(actor.Id, "registration.rejected", $"registration:{request.Id}", new { reason });
                    await store.Db.SaveChangesAsync(cancellationToken);
                    return Unit.Value;
                });
        });
    }
}

internal sealed class GetRegistrationRequestsHandler(AccessDbContext db) : IQueryHandler<GetRegistrationRequests, IReadOnlyList<RegistrationSummary>>
{
    public const int MaxItems = 200;

    public async Task<IReadOnlyList<RegistrationSummary>> HandleAsync(GetRegistrationRequests query, CancellationToken cancellationToken = default)
    {
        var requests = db.Registrations.AsNoTracking();
        if (RegistrationViews.StatusOf(query.Status) is { } status)
        {
            requests = requests.Where(r => r.Status == status);
        }

        return [.. (await requests.OrderByDescending(r => r.SubmittedAt).Take(MaxItems).ToListAsync(cancellationToken))
            .Select(row => row.ToDomain())
            .Select(r => new RegistrationSummary(r.Id, r.Company.Name, r.Company.TaxId.Value, r.Applicant.Name.Value, r.Applicant.Email.Value, r.SubmittedAt, RegistrationViews.StatusName(r.Status)))];
    }
}

internal sealed class GetRegistrationRequestHandler(AccessDbContext db) : IQueryHandler<GetRegistrationRequest, Result<RegistrationDetails, Error>>
{
    public async Task<Result<RegistrationDetails, Error>> HandleAsync(GetRegistrationRequest query, CancellationToken cancellationToken = default)
    {
        var request = (await db.Registrations.AsNoTracking().SingleOrDefaultAsync(r => r.Id == query.Id, cancellationToken))?.ToDomain();
        if (request is null)
        {
            return Error.NotFound("registration.not-found", "The request does not exist.");
        }

        var (decidedById, decidedAt, customer, reason) = request.Status switch
        {
            RegistrationStatus.Approved a => ((Guid?)a.DecidedBy, (DateTimeOffset?)a.At, a.CustomerCode, (string?)null),
            RegistrationStatus.Rejected r => (r.DecidedBy, r.At, null, r.Reason),
            _ => (null, null, null, null),
        };
        var decidedBy = decidedById is { } id ? await db.Accounts.AsNoTracking().Where(a => a.Id == id).Select(a => a.Name).SingleOrDefaultAsync(cancellationToken) : null;
        var (company, applicant) = (request.Company, request.Applicant);
        return new RegistrationDetails(
            request.Id,
            company.Name,
            company.TaxId.Value,
            company.Address.Street,
            company.Address.PostalCode.Value,
            company.Address.City,
            company.Address.Country.Value,
            applicant.Name.Value,
            applicant.Email.Value,
            applicant.Phone,
            applicant.Culture.Value,
            request.SubmittedAt,
            RegistrationViews.StatusName(request.Status),
            decidedBy,
            decidedAt,
            customer,
            reason);
    }
}

internal static class RegistrationViews
{
    public static string StatusName(RegistrationStatus status) =>
        status switch
        {
            RegistrationStatus.Approved => "approved",
            RegistrationStatus.Rejected => "rejected",
            _ => "pending",
        };

    /// <summary>The stored status for a status filter; null for no filter.</summary>
    public static string? StatusOf(string? filter) =>
        filter switch
        {
            "pending" => "Pending",
            "approved" => "Approved",
            "rejected" => "Rejected",
            _ => null,
        };
}
