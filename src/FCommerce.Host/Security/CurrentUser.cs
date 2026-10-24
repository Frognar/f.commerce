using System.Security.Claims;
using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Authorization.Core;
using FCommerce.Customers.Contracts;
using FCommerce.Pricing.Contracts;
using FCommerce.Settings.Contracts;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Features;
using FCommerce.SharedKernel.Monetary;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Host.Security;

/// <summary>The signed-in user of the current request, read once per request from the Access module.</summary>
public sealed class CurrentUser(
    IHttpContextAccessor accessor,
    IQueryHandler<GetSession, Result<SessionView, Error>> sessions,
    IQueryHandler<GetCustomers, IReadOnlyList<CustomerSummary>> customers,
    IQueryHandler<GetPlatformSettings, PlatformSettingsView> settings)
{
    public const string StampClaim = "fc:stamp";

    /// <summary>The customer the user chose to act for; only a preference, checked against the memberships on every request.</summary>
    public const string CustomerCookie = "fc.customer";

    internal const string SessionItem = "fc:session";
    private Maybe<IReadOnlyList<CustomerSummary>> companies;

    private HttpContext Http => accessor.HttpContext ?? throw new InvalidOperationException("No HTTP context.");

    public bool IsSignedIn => Http.User.Identity?.IsAuthenticated == true;

    public Maybe<Guid> UserId => Guid.TryParse(Http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Maybe<Guid>.None;

    /// <summary>The signed-in user on pages behind an authorization policy (no user there is a misconfiguration).</summary>
    public Guid RequiredUserId => UserId.Match(id => id, () => throw new InvalidOperationException("This page requires a signed-in user."));

    /// <summary>The session's account; none for anonymous requests or ended sessions.</summary>
    public async Task<Maybe<SessionView>> SessionAsync(CancellationToken cancellationToken = default)
    {
        if (Http.Items.TryGetValue(SessionItem, out var cached) && cached is Maybe<SessionView> known)
        {
            return known;
        }

        var query = from id in UserId from stamp in Http.User.FindFirstValue(StampClaim).ToMaybe() select new GetSession(id, stamp);
        var session = await query.Match(
            async request => (await sessions.HandleAsync(request, cancellationToken)).ToMaybe(),
            () => Task.FromResult(Maybe<SessionView>.None));
        Http.Items[SessionItem] = session;
        return session;
    }

    /// <summary>
    /// The membership the user acts under: the chosen customer (with the multi-customer feature on) or the first one.
    /// None for anonymous users, operators and users without customers.
    /// </summary>
    public async Task<Maybe<MembershipView>> ActiveMembershipAsync(CancellationToken cancellationToken = default)
    {
        var customerUser = (await SessionAsync(cancellationToken)).Where(s => s.Operator is null && s.Memberships.Count > 0);
        return await customerUser.Match(
            async session =>
            {
                var multiCustomer = (await settings.HandleAsync(new GetPlatformSettings(), cancellationToken)).IsOn(Feature.MultiCustomerAccounts.Value);
                var chosen = Http.Request.Cookies[CustomerCookie].ToMaybe().Where(_ => multiCustomer);
                return session.Memberships.FirstOrNone(m => chosen.Exists(code => code == m.CustomerCode)).Or(() => session.Memberships.FirstOrNone());
            },
            () => Task.FromResult(Maybe<MembershipView>.None));
    }

    /// <summary>
    /// Who looks at prices, only from the session: anonymous, signed in without a customer (operators, users without
    /// memberships) or acting for the active customer (the chosen one is checked against the memberships).
    /// </summary>
    public async Task<PriceViewer> PriceViewerAsync(CancellationToken cancellationToken = default) =>
        await (await SessionAsync(cancellationToken)).Match(
            async _ => new PriceViewer(true, (await ActiveMembershipAsync(cancellationToken)).Map(m => m.CustomerCode).OrNull()),
            () => Task.FromResult(PriceViewer.Anonymous));

    /// <summary>The customers of the user's memberships (names for the company switcher, active flags for ABAC).</summary>
    public async Task<IReadOnlyList<CustomerSummary>> CompaniesAsync(CancellationToken cancellationToken = default)
    {
        var known = await companies.Match(
            Task.FromResult,
            async () =>
            {
                var codes = (await SessionAsync(cancellationToken)).Where(s => s.Operator is null).Match(s => s.Memberships.Select(m => m.CustomerCode).ToList(), () => []);
                return codes.Count > 0 ? await customers.HandleAsync(new GetCustomers(codes), cancellationToken) : [];
            });
        companies = Maybe.Some(known);
        return known;
    }

    /// <summary>The ABAC subject: operator, customer user (for the active customer), signed-in user or anonymous.</summary>
    public async Task<Subject> SubjectAsync(CancellationToken cancellationToken = default) =>
        await (await SessionAsync(cancellationToken)).Match(
            session => session.Operator.ToMaybe().Match(
                op => Task.FromResult<Subject>(new Subject.Operator(
                    session.UserId,
                    EmailAddress.Parse(session.Email).Match(e => e, e => throw new InvalidOperationException(e.Message)),
                    new OperatorAttributes(op.CanManageSettings, op.CanManageOperators, op.CanDecideRegistrations, op.CustomerScope == "all" ? CustomerScope.All : CustomerScope.AsSalesperson))),
                async () => (await ActiveMembershipAsync(cancellationToken)).Match<Subject>(
                    membership => new Subject.CustomerUser(session.UserId, membership.CustomerCode, Attributes(membership)),
                    () => new Subject.SignedInUser(session.UserId))),
            () => Task.FromResult(Subject.Anonymous));

    private static MembershipAttributes Attributes(MembershipView view) =>
        new(
            view.CanOrder,
            view.OrderScope == "company" ? OrderScope.Company : OrderScope.Own,
            from amount in view.OrderLimit.ToMaybe()
            from currency in view.OrderLimitCurrency.ToMaybe().Bind(code => Currency.Parse(code).ToMaybe())
            select new Money(amount, currency),
            view.CanViewDocuments,
            view.CanManageUsers,
            view.DeliveryAddressCodes.ToMaybe().Match(codes => DeliveryAddressScope.Only([.. codes]), () => DeliveryAddressScope.All));
}

/// <summary>The policy decision point for the current request; the environment comes from the platform settings.</summary>
public sealed class AccessControl(CurrentUser user, IQueryHandler<GetPlatformSettings, PlatformSettingsView> settings, TimeProvider time)
{
    public async Task<Decision> DecideAsync(AccessAction action, Resource resource, CancellationToken cancellationToken = default)
    {
        var current = await settings.HandleAsync(new GetPlatformSettings(), cancellationToken);
        var subject = await user.SubjectAsync(cancellationToken);
        var activeCustomerActive = subject is not Subject.CustomerUser customerUser
            || (await user.CompaniesAsync(cancellationToken)).FirstOrNone(c => c.Code == customerUser.ActiveCustomer).Exists(c => c.Active);
        return PlatformPolicies.Decide(new AccessRequest(
            subject,
            action,
            resource,
            new AccessEnvironment(
                time.GetUtcNow(),
                Enum.Parse<CatalogVisibility>(current.CatalogVisibility),
                [.. current.Features.Where(f => f.On).Choose(f => FeatureId.Parse(f.Id).ToMaybe())],
                activeCustomerActive)));
    }

    public async Task<bool> CanAsync(AccessAction action, Resource resource, CancellationToken cancellationToken = default) =>
        (await DecideAsync(action, resource, cancellationToken)).IsPermitted;
}
