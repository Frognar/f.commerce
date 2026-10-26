using System.Collections.Immutable;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Features;

namespace FCommerce.Authorization.Core;

/// <summary>Who asks.</summary>
public abstract record Subject
{
    private Subject()
    {
    }

    public static Subject Anonymous { get; } = new AnonymousSubject();

    public sealed record AnonymousSubject : Subject;

    /// <summary>Signed in, but neither acting for a customer nor an operator: may use the catalog and their own account.</summary>
    public sealed record SignedInUser(Guid UserId) : Subject;

    /// <summary>A customer's employee acting for <paramref name="ActiveCustomer"/> (one of their memberships).</summary>
    public sealed record CustomerUser(Guid UserId, string ActiveCustomer, MembershipAttributes Attributes) : Subject;

    /// <summary>A seller's staff member in the back-office.</summary>
    public sealed record Operator(Guid UserId, EmailAddress Email, OperatorAttributes Attributes) : Subject;
}

/// <summary>What is asked about. Customer-owned resources carry the customer and its salesperson (for operator scopes).</summary>
public abstract record Resource
{
    private Resource()
    {
    }

    public sealed record Catalog : Resource;

    /// <summary>The signed-in user's own account (profile, password, language).</summary>
    public sealed record OwnAccount : Resource;

    public sealed record CustomerProfile(string CustomerCode, Maybe<EmailAddress> Salesperson) : Resource;

    public sealed record CustomerUsers(string CustomerCode, Maybe<EmailAddress> Salesperson) : Resource;

    public sealed record CustomerDocuments(string CustomerCode, Maybe<EmailAddress> Salesperson) : Resource;

    /// <summary>The cart of the signed-in user for a customer.</summary>
    public sealed record Cart(string CustomerCode) : Resource;

    /// <summary>The shopping lists of the signed-in user for a customer.</summary>
    public sealed record ShoppingLists(string CustomerCode) : Resource;

    /// <summary>The orders of a customer: placing one, the order history.</summary>
    public sealed record Orders(string CustomerCode, Maybe<EmailAddress> Salesperson) : Resource;

    /// <summary>One order of a customer, placed by <paramref name="PlacedBy"/>.</summary>
    public sealed record Order(string CustomerCode, Maybe<EmailAddress> Salesperson, Guid PlacedBy) : Resource;

    /// <summary>The API keys of a customer: creating, listing and revoking them.</summary>
    public sealed record ApiKeys(string CustomerCode, Maybe<EmailAddress> Salesperson) : Resource;

    /// <summary>The back-office list of customers.</summary>
    public sealed record Customers : Resource;

    public sealed record PlatformSettings : Resource;

    /// <summary>Registration requests: submitting one (public form) or reading/deciding them (operators).</summary>
    public sealed record Registrations : Resource;

    public sealed record Operators : Resource;

    /// <summary>The customer a customer-owned resource belongs to, if any.</summary>
    public Maybe<(string Code, Maybe<EmailAddress> Salesperson)> Owner =>
        this switch
        {
            CustomerProfile r => (r.CustomerCode, r.Salesperson),
            CustomerUsers r => (r.CustomerCode, r.Salesperson),
            CustomerDocuments r => (r.CustomerCode, r.Salesperson),
            Cart r => (r.CustomerCode, Maybe<EmailAddress>.None),
            ShoppingLists r => (r.CustomerCode, Maybe<EmailAddress>.None),
            ApiKeys r => (r.CustomerCode, r.Salesperson),
            Orders r => (r.CustomerCode, r.Salesperson),
            Order r => (r.CustomerCode, r.Salesperson),
            _ => Maybe<(string, Maybe<EmailAddress>)>.None,
        };
}

public enum AccessAction
{
    Read,
    Manage,
    Submit,
    Decide,
    Download,
}

public enum CatalogVisibility
{
    Public,
    SignedInOnly,
}

/// <param name="ActiveCustomerActive">Whether the subject's active customer is active in the ERP (true when not applicable).</param>
public sealed record AccessEnvironment(DateTimeOffset Now, CatalogVisibility CatalogVisibility, ImmutableHashSet<FeatureId> FeaturesOn, bool ActiveCustomerActive = true);

public sealed record AccessRequest(Subject Subject, AccessAction Action, Resource Resource, AccessEnvironment Environment);
