using System.Security.Claims;
using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Authorization.Core;
using FCommerce.Customers.Contracts;
using FCommerce.Settings.Contracts;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Features;
using FCommerce.SharedKernel.Monetary;
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
    private IReadOnlyList<CustomerSummary>? companies;

    private HttpContext Http => accessor.HttpContext ?? throw new InvalidOperationException("No HTTP context.");

    public bool IsSignedIn => Http.User.Identity?.IsAuthenticated == true;

    public Guid? UserId => Guid.TryParse(Http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    /// <summary>The session's account; null for anonymous requests or ended sessions.</summary>
    public async Task<SessionView?> SessionAsync(CancellationToken cancellationToken = default)
    {
        if (Http.Items.TryGetValue(SessionItem, out var cached))
        {
            return cached as SessionView;
        }

        var session = UserId is { } id && Http.User.FindFirstValue(StampClaim) is { } stamp
            ? (await sessions.HandleAsync(new GetSession(id, stamp), cancellationToken)).Match(s => (SessionView?)s, _ => null)
            : null;
        Http.Items[SessionItem] = session;
        return session;
    }

    /// <summary>
    /// The membership the user acts under: the chosen customer (with the multi-customer feature on) or the first one.
    /// Null for anonymous users, operators and users without customers.
    /// </summary>
    public async Task<MembershipView?> ActiveMembershipAsync(CancellationToken cancellationToken = default)
    {
        if (await SessionAsync(cancellationToken) is not { Operator: null, Memberships: [var first, ..] } session)
        {
            return null;
        }

        var multiCustomer = (await settings.HandleAsync(new GetPlatformSettings(), cancellationToken)).IsOn(Feature.MultiCustomerAccounts.Value);
        var chosen = multiCustomer ? Http.Request.Cookies[CustomerCookie] : null;
        return session.Memberships.FirstOrDefault(m => m.CustomerCode == chosen) ?? first;
    }

    /// <summary>The customers of the user's memberships (names for the company switcher, active flags for ABAC).</summary>
    public async Task<IReadOnlyList<CustomerSummary>> CompaniesAsync(CancellationToken cancellationToken = default)
    {
        if (companies is null)
        {
            var session = await SessionAsync(cancellationToken);
            companies = session is { Operator: null, Memberships.Count: > 0 }
                ? await customers.HandleAsync(new GetCustomers([.. session.Memberships.Select(m => m.CustomerCode)]), cancellationToken)
                : [];
        }

        return companies;
    }

    /// <summary>The ABAC subject: operator, customer user (for the active customer), signed-in user or anonymous.</summary>
    public async Task<Subject> SubjectAsync(CancellationToken cancellationToken = default) =>
        await SessionAsync(cancellationToken) switch
        {
            null => Subject.Anonymous,
            { Operator: { } op } session => new Subject.Operator(
                session.UserId,
                EmailAddress.Parse(session.Email).Match(e => e, e => throw new InvalidOperationException(e.Message)),
                new OperatorAttributes(op.CanManageSettings, op.CanManageOperators, op.CanDecideRegistrations, op.CustomerScope == "all" ? CustomerScope.All : CustomerScope.AsSalesperson)),
            var session => await ActiveMembershipAsync(cancellationToken) is { } membership
                ? new Subject.CustomerUser(session.UserId, membership.CustomerCode, Attributes(membership))
                : new Subject.SignedInUser(session.UserId),
        };

    private static MembershipAttributes Attributes(MembershipView view) =>
        new(
            view.CanOrder,
            view.OrderScope == "company" ? OrderScope.Company : OrderScope.Own,
            view is { OrderLimit: { } amount, OrderLimitCurrency: { } currency } ? Currency.Parse(currency).Match(c => (Money?)new Money(amount, c), _ => null) : null,
            view.CanViewDocuments,
            view.CanManageUsers,
            view.DeliveryAddressCodes is { } codes ? DeliveryAddressScope.Only([.. codes]) : DeliveryAddressScope.All);
}

/// <summary>The policy decision point for the current request; the environment comes from the platform settings.</summary>
public sealed class AccessControl(CurrentUser user, IQueryHandler<GetPlatformSettings, PlatformSettingsView> settings, TimeProvider time)
{
    public async Task<Decision> DecideAsync(AccessAction action, Resource resource, CancellationToken cancellationToken = default)
    {
        var current = await settings.HandleAsync(new GetPlatformSettings(), cancellationToken);
        var subject = await user.SubjectAsync(cancellationToken);
        var activeCustomerActive = subject is not Subject.CustomerUser customerUser
            || (await user.CompaniesAsync(cancellationToken)).FirstOrDefault(c => c.Code == customerUser.ActiveCustomer)?.Active == true;
        return PlatformPolicies.Decide(new AccessRequest(
            subject,
            action,
            resource,
            new AccessEnvironment(
                time.GetUtcNow(),
                Enum.Parse<CatalogVisibility>(current.CatalogVisibility),
                [.. current.Features.Where(f => f.On).Select(f => FeatureId.Parse(f.Id)).SelectMany(r => r.Match(id => (FeatureId[])[id], _ => []))],
                activeCustomerActive)));
    }

    public async Task<bool> CanAsync(AccessAction action, Resource resource, CancellationToken cancellationToken = default) =>
        (await DecideAsync(action, resource, cancellationToken)).IsPermitted;
}
