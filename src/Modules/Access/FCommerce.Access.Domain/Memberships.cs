using FCommerce.Authorization.Core;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Access.Domain;

/// <summary>Who gave the membership. The ERP manages only memberships it created; manual ones survive ERP changes.</summary>
public enum MembershipSource
{
    Erp,
    Operator,
    CustomerAdmin,
    Registration,
}

/// <summary>A user's right to act for a customer, with the attributes the access policies use.</summary>
/// <param name="ErpContactId">The ERP contact the membership came from (ERP source only).</param>
public sealed record Membership(Guid UserId, string CustomerCode, MembershipAttributes Attributes, MembershipSource Source, Maybe<string> ErpContactId);

/// <summary>The rights edited in the UI; order limit and delivery addresses keep their values.</summary>
public sealed record MembershipRights(bool CanOrder, OrderScope OrderScope, bool CanViewDocuments, bool CanManageUsers)
{
    public static MembershipRights Of(MembershipAttributes attributes) =>
        new(attributes.CanOrder, attributes.OrderScope, attributes.CanViewDocuments, attributes.CanManageUsers);

    public MembershipAttributes ApplyTo(MembershipAttributes attributes) =>
        attributes with { CanOrder = CanOrder, OrderScope = OrderScope, CanViewDocuments = CanViewDocuments, CanManageUsers = CanManageUsers };
}

public static class MembershipRules
{
    /// <summary>
    /// Whether an account (none: a new one) may become a member of <paramref name="customerCode"/>. Operators act in the
    /// back-office only; without the multi-customer feature a user belongs to one customer.
    /// </summary>
    public static Result<Membership, Error> Admit(
        Maybe<Account> account,
        IReadOnlyCollection<Membership> memberships,
        bool multiCustomer,
        Membership requested)
    {
        if (account.Exists(a => a.IsOperator))
        {
            return Error.Conflict("membership.operator-account", "Operator accounts cannot act for customers.");
        }

        if (account.Exists(a => a.Status == AccountStatus.Disabled))
        {
            return Error.Conflict("membership.account-disabled", "This account is disabled.");
        }

        if (memberships.Any(m => m.CustomerCode == requested.CustomerCode))
        {
            return Error.Conflict("membership.exists", "This person already has access to the customer.");
        }

        return !multiCustomer && memberships.Count > 0
            ? Error.Conflict("membership.single-customer", "This person already acts for another customer.")
            : requested;
    }

    /// <summary>Members other than <paramref name="except"/> who can manage users (for the last-administrator rule).</summary>
    public static int OtherAdministrators(IEnumerable<Membership> customerMemberships, Guid except) =>
        customerMemberships.Count(m => m.UserId != except && m.Attributes.CanManageUsers);

    /// <summary>The attributes for a template name; unknown names fall back to the buyer template.</summary>
    public static MembershipAttributes FromTemplate(Maybe<string> template) =>
        template.Bind(MembershipAttributes.Templates.GetValueOrNone).ValueOr(MembershipAttributes.Buyer);
}

/// <summary>
/// Keeping platform access in line with the ERP contacts of a customer: contacts with portal access get a membership
/// (new people an account and an invitation), ERP memberships of people no longer listed are revoked.
/// </summary>
public static class ErpContactSync
{
    /// <summary>A contact with portal access, as validated by Access.</summary>
    public sealed record Contact(string ExternalId, EmailAddress Email, PersonName Name, MembershipAttributes Attributes);

    /// <summary>A stored account with its memberships (of any customer).</summary>
    public sealed record Member(Account Account, IReadOnlyList<Membership> Memberships);

    public abstract record Step
    {
        private Step()
        {
        }

        /// <summary>Nobody has this e-mail: create the account and invite the person.</summary>
        public sealed record CreateAccount(Contact Contact) : Step;

        /// <summary>An existing account gets access to the customer.</summary>
        public sealed record Grant(Account Account, Membership Membership) : Step;

        public sealed record Revoke(Membership Membership, EmailAddress Email) : Step;

        /// <summary>The contact cannot get access (operator account, disabled account, single-customer rule).</summary>
        public sealed record Skip(Contact Contact, Error Reason) : Step;
    }

    /// <param name="contacts">Contacts with portal access, in ERP order.</param>
    /// <param name="existing">Accounts with these e-mails and accounts that are members of the customer.</param>
    public static IReadOnlyList<Step> Plan(string customerCode, IReadOnlyList<Contact> contacts, IReadOnlyCollection<Member> existing, bool multiCustomer)
    {
        var byEmail = existing.ToDictionary(m => m.Account.Email);
        var wanted = contacts.DistinctBy(c => c.Email).ToList();

        var additions = wanted
            .Where(contact => !(byEmail.TryGetValue(contact.Email, out var member) && member.Memberships.Any(m => m.CustomerCode == customerCode)))
            .Select(contact => byEmail.TryGetValue(contact.Email, out var member)
                ? MembershipRules.Admit(member.Account, member.Memberships, multiCustomer, ErpMembership(member.Account.Id, customerCode, contact))
                    .Match<Step>(membership => new Step.Grant(member.Account, membership), error => new Step.Skip(contact, error))
                : new Step.CreateAccount(contact));

        var emails = wanted.Select(c => c.Email).ToHashSet();
        var revocations = existing
            .SelectMany(member => member.Memberships
                .Where(m => m.CustomerCode == customerCode && m.Source == MembershipSource.Erp && !emails.Contains(member.Account.Email))
                .Select(m => new Step.Revoke(m, member.Account.Email)));

        return [.. additions, .. revocations];
    }

    public static Membership ErpMembership(Guid userId, string customerCode, Contact contact) =>
        new(userId, customerCode, contact.Attributes, MembershipSource.Erp, contact.ExternalId);
}
