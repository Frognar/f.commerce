using System.Security.Claims;
using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Authorization.Core;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Host.Security;

/// <summary>The signed-in user of the current request, read once per request from the Access module.</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor, IQueryHandler<GetSession, Result<SessionView, Error>> sessions)
{
    public const string StampClaim = "fc:stamp";
    internal const string SessionItem = "fc:session";

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

    /// <summary>The ABAC subject: operator, signed-in user or anonymous (customer users come with memberships).</summary>
    public async Task<Subject> SubjectAsync(CancellationToken cancellationToken = default) =>
        await SessionAsync(cancellationToken) switch
        {
            null => Subject.Anonymous,
            { Operator: { } op } session => new Subject.Operator(
                session.UserId,
                EmailAddress.Parse(session.Email).Match(e => e, e => throw new InvalidOperationException(e.Message)),
                new OperatorAttributes(op.CanManageSettings, op.CanManageOperators, op.CanDecideRegistrations, op.CustomerScope == "all" ? CustomerScope.All : CustomerScope.AsSalesperson)),
            var session => new Subject.SignedInUser(session.UserId),
        };
}

/// <summary>The policy decision point for the current request.</summary>
public sealed class AccessControl(CurrentUser user, IPlatformState platform, TimeProvider time)
{
    public async Task<Decision> DecideAsync(AccessAction action, Resource resource, CancellationToken cancellationToken = default) =>
        PlatformPolicies.Decide(new AccessRequest(
            await user.SubjectAsync(cancellationToken),
            action,
            resource,
            new AccessEnvironment(time.GetUtcNow(), platform.CatalogVisibility, platform.Features.On)));

    public async Task<bool> CanAsync(AccessAction action, Resource resource, CancellationToken cancellationToken = default) =>
        (await DecideAsync(action, resource, cancellationToken)).IsPermitted;
}
