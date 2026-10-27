using FCommerce.Access.Contracts;
using FCommerce.Access.Domain;
using FCommerce.Access.Persistence;
using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Authorization.Core;
using FCommerce.Customers.Contracts;
using FCommerce.Infrastructure.Persistence;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using FCommerce.SharedKernel.Validation;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Access.Handlers;

internal sealed class SubmitRegistrationHandler(AccessStore store) : ICommandHandler<SubmitRegistration, Result<Guid, Error>>
{
    public async Task<Result<Guid, Error>> HandleAsync(SubmitRegistration command, CancellationToken cancellationToken = default) =>
        await Registrations.Validate(new UnvalidatedRegistration(
                command.CompanyName.OrEmpty(), command.TaxId.OrEmpty(), command.Street.OrEmpty(), command.PostalCode.OrEmpty(), command.City.OrEmpty(),
                command.Country.OrEmpty(), command.PersonName.OrEmpty(), command.Email.OrEmpty(), command.Phone.ToMaybe(), command.Culture.OrEmpty(),
                command.AcceptedTerms))
            .ToResult()
            .BindAsync(async form =>
            {
                var request = Registrations.Submit(Guid.CreateVersion7(), form.Company, form.Applicant, store.Now);
                store.Db.Registrations.Add(RegistrationRow.From(request));
                return await store.Enqueue(new RegistrationSubmitted(request.Id, request.Company.Name.Value, request.Company.TaxId.Value, request.Applicant.Name.Value, request.Applicant.Email.Value))
                    .MapAsync(async _ =>
                    {
                        store.Audit(AccessStore.SystemActor, "registration.submitted", $"registration:{request.Id}", new { email = request.Applicant.Email.Value, taxId = request.Company.TaxId.Value });
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
        var row = await store.Db.Registrations.SingleOrNoneAsync(r => r.Id == command.RequestId, cancellationToken);
        var decider = await RegistrationViews.DeciderAsync(store, command.ActorId, cancellationToken);
        return await row.Match(
            stored => decider.Match(
                _ => MembershipAttributes.Templates.GetValueOrNone(command.Template.OrEmpty()).Match(
                    template => ApproveAsync(stored, template, command, cancellationToken),
                    () => Task.FromResult(Result.Fail<Unit, Error>(
                        Error.Validation("registration.template-unknown", "Choose the rights for the applicant.") with { Field = "template" }))),
                () => Task.FromResult(Result.Fail<Unit, Error>(
                    Error.Forbidden("grant.not-allowed", "Only operators who decide registrations can approve them.")))),
            () => Task.FromResult(Result.Fail<Unit, Error>(RegistrationViews.NotFound())));
    }

    private async Task<Result<Unit, Error>> ApproveAsync(RegistrationRow row, MembershipAttributes template, ApproveRegistration command, CancellationToken cancellationToken)
    {
        var request = row.ToDomain();
        return await (await customers.HandleAsync(new GetCustomer(command.CustomerCode.OrEmpty()), cancellationToken)).BindAsync(async customer =>
            await Registrations.Approve(request, command.ActorId, CustomerCodes.Of(customer), store.Now).BindAsync(async approved =>
            {
                var invitee = new CustomerInvitee(request.Applicant.Email, request.Applicant.Name, request.Applicant.Culture, template);
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
        var row = await store.Db.Registrations.SingleOrNoneAsync(r => r.Id == command.RequestId, cancellationToken);
        var decider = await RegistrationViews.DeciderAsync(store, command.ActorId, cancellationToken);
        return await row.Match(
            stored => decider.Match(
                actor => RejectAsync(stored, actor, command.Reason.OrEmpty(), cancellationToken),
                () => Task.FromResult(Result.Fail<Unit, Error>(
                    Error.Forbidden("grant.not-allowed", "Only operators who decide registrations can reject them.")))),
            () => Task.FromResult(Result.Fail<Unit, Error>(RegistrationViews.NotFound())));
    }

    private async Task<Result<Unit, Error>> RejectAsync(RegistrationRow row, Account actor, string reasonText, CancellationToken cancellationToken)
    {
        var request = row.ToDomain();
        return await Registrations.Reject(request, actor.Id, reasonText, store.Now).BindAsync(async decided =>
        {
            var (rejected, reason) = (decided.Request, decided.Reason.Value);
            row.Apply(rejected);
            return await store.Enqueue(new RegistrationRejected(request.Id, request.Applicant.Email.Value, request.Applicant.Name.Value, request.Applicant.Culture.Value, request.Company.Name.Value, reason))
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
        var all = db.Registrations.AsNoTracking();
        var filter = query.Status.ToMaybe().Map(s => s.Trim()).Where(s => s.Length > 0);
        if (filter.Exists(s => !RegistrationViews.StatusOf(s).Exists(_ => true)))
        {
            return [];
        }

        var requests = filter.Bind(RegistrationViews.StatusOf).Match(status => all.Where(r => r.Status == status), () => all);

        return [.. (await requests.OrderByDescending(r => r.SubmittedAt).Take(MaxItems).ToListAsync(cancellationToken))
            .Select(row => row.ToDomain())
            .Select(r => new RegistrationSummary(r.Id, r.Company.Name.Value, r.Company.TaxId.Value, r.Applicant.Name.Value, r.Applicant.Email.Value, r.SubmittedAt, RegistrationViews.StatusName(r.Status)))];
    }
}

internal sealed class GetRegistrationRequestHandler(AccessDbContext db) : IQueryHandler<GetRegistrationRequest, Result<RegistrationDetails, Error>>
{
    public async Task<Result<RegistrationDetails, Error>> HandleAsync(GetRegistrationRequest query, CancellationToken cancellationToken = default)
    {
        var row = await db.Registrations.AsNoTracking().SingleOrNoneAsync(r => r.Id == query.Id, cancellationToken);
        return await row.Match(
            async stored => Result.Ok<RegistrationDetails, Error>(await DetailsAsync(stored.ToDomain(), cancellationToken)),
            () => Task.FromResult(Result.Fail<RegistrationDetails, Error>(RegistrationViews.NotFound())));
    }

    private async Task<RegistrationDetails> DetailsAsync(RegistrationRequest request, CancellationToken cancellationToken)
    {
        var (decidedById, decidedAt, customer, reason) = request.Status switch
        {
            RegistrationStatus.Approved a => (Maybe.Some(a.DecidedBy), Maybe.Some(a.At), Maybe.Some(a.CustomerCode.Value), Maybe<string>.None),
            RegistrationStatus.Rejected r => (Maybe.Some(r.DecidedBy), Maybe.Some(r.At), Maybe<string>.None, Maybe.Some(r.Reason.Value)),
            _ => (Maybe<Guid>.None, Maybe<DateTimeOffset>.None, Maybe<string>.None, Maybe<string>.None),
        };
        var decidedBy = await decidedById.Match(
            id => db.Accounts.AsNoTracking().Where(a => a.Id == id).Select(a => a.Name).FirstOrNoneAsync(cancellationToken),
            () => Task.FromResult(Maybe<string>.None));
        var (company, applicant) = (request.Company, request.Applicant);
        return new RegistrationDetails(
            request.Id,
            company.Name.Value,
            company.TaxId.Value,
            company.Address.Street.Value,
            company.Address.PostalCode.Value,
            company.Address.City.Value,
            company.Address.Country.Value,
            applicant.Name.Value,
            applicant.Email.Value,
            applicant.Phone.Map(p => p.Value).OrNull(),
            applicant.Culture.Value,
            request.SubmittedAt,
            RegistrationViews.StatusName(request.Status),
            decidedBy.OrNull(),
            decidedAt.OrNull(),
            customer.OrNull(),
            reason.OrNull());
    }
}

internal static class RegistrationViews
{
    public static string StatusName(RegistrationStatus status) =>
        status switch
        {
            RegistrationStatus.Approved => "approved",
            RegistrationStatus.Rejected => "rejected",
            RegistrationStatus.PendingDecision => "pending",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "A registration status without a name."),
        };

    /// <summary>The stored status for a status filter; none for an unknown one.</summary>
    public static Maybe<string> StatusOf(string filter) =>
        filter switch
        {
            "pending" => "Pending",
            "approved" => "Approved",
            "rejected" => "Rejected",
            _ => Maybe<string>.None,
        };

    public static Error NotFound() => Error.NotFound("registration.not-found", "The request does not exist.");

    /// <summary>The actor, when it is an active operator who decides registrations.</summary>
    public static async Task<Maybe<Account>> DeciderAsync(AccessStore store, Guid actorId, CancellationToken cancellationToken) =>
        (await store.FindAsync(actorId, cancellationToken))
            .Map(row => row.ToDomain())
            .Where(actor => AccessStore.AsOperator(actor).Exists(op => op.Attributes.CanDecideRegistrations));
}
