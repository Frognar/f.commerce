using FCommerce.Authorization.Core;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Access.Domain;

/// <summary>Who gave the membership. The ERP manages only memberships it created; manual ones survive ERP changes.</summary>
public abstract record MembershipSource
{
    private MembershipSource()
    {
    }

    public static MembershipSource Operator { get; } = new ByOperator();

    public static MembershipSource CustomerAdmin { get; } = new ByCustomerAdmin();

    public static MembershipSource Registration { get; } = new ByRegistration();

    /// <summary>"Erp", "Operator", "CustomerAdmin" or "Registration".</summary>
    public abstract string Name { get; }

    /// <summary>Given by the ERP for one of the customer's contact persons.</summary>
    public sealed record Erp(ErpContactId Contact) : MembershipSource
    {
        public override string Name => "Erp";
    }

    public sealed record ByOperator : MembershipSource
    {
        public override string Name => "Operator";
    }

    public sealed record ByCustomerAdmin : MembershipSource
    {
        public override string Name => "CustomerAdmin";
    }

    public sealed record ByRegistration : MembershipSource
    {
        public override string Name => "Registration";
    }
}

/// <summary>A user's right to act for a customer, with the attributes the access policies use.</summary>
public sealed record Membership(Guid UserId, CustomerCode CustomerCode, MembershipAttributes Attributes, MembershipSource Source);

/// <summary>The rights edited in the UI, read into membership attributes.</summary>
public static class MembershipRights
{
    public const int MaxDeliveryAddresses = 100;

    /// <summary>
    /// The attributes for the edited rights. A limit and allowed addresses belong to the right to order: without it
    /// they are dropped. No addresses listed means all of them.
    /// </summary>
    public static Result<MembershipAttributes, Error> Parse(
        bool canOrder, OrderScope orderScope, bool canViewDocuments, bool canManageUsers, Maybe<decimal> orderLimit, string orderLimitCurrency, Maybe<IReadOnlyList<string>> deliveryAddressCodes) =>
        canOrder
            ? from limit in orderLimit.Match(
                  amount => OrderLimit.Parse(amount, orderLimitCurrency).Map(Maybe.Some),
                  () => Result.Ok<Maybe<OrderLimit>, Error>(Maybe<OrderLimit>.None))
              from addresses in Addresses(deliveryAddressCodes)
              select new MembershipAttributes(new OrderingRight.Allowed(limit, addresses), orderScope, canViewDocuments, canManageUsers)
            : new MembershipAttributes(OrderingRight.NotAllowed, orderScope, canViewDocuments, canManageUsers);

    private static Result<DeliveryAddressScope, Error> Addresses(Maybe<IReadOnlyList<string>> codes)
    {
        var scope = DeliveryAddressScope.Of(codes.ValueOr([]));
        return scope is DeliveryAddressScope.OnlyAddresses { Codes.Count: > MaxDeliveryAddresses }
            ? Error.Validation("membership.too-many-addresses", $"At most {MaxDeliveryAddresses} delivery addresses.") with { Field = "deliveryAddressCodes" }
            : scope;
    }
}

public static class MembershipRules
{
    /// <summary>
    /// Whether an account (none: a new one) may become a member of the customer. Operators act in the back-office
    /// only; without the multi-customer feature a user belongs to one customer.
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

        if (account.Exists(a => a.State is AccountState.Disabled))
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
}

/// <summary>Operator rights of an account: staff members do not act for customers, so a customer user is not made one.</summary>
public static class OperatorRules
{
    /// <summary>Gives (or changes) operator rights; the stamp stays, the operator keeps their sessions.</summary>
    public static Result<Account, Error> Appoint(Account account, IReadOnlyCollection<Membership> memberships, OperatorAttributes attributes) =>
        account.State is AccountState.Disabled ? Error.Conflict("operator.account-disabled", "This account is disabled.")
        : memberships.Count > 0 ? Error.Conflict("operator.customer-user", "This person acts for customers; an operator account cannot.")
        : account.WithOperator(attributes, account.SecurityStamp);

    /// <summary>Takes operator rights away and signs the account out everywhere.</summary>
    public static Account Revoke(Account account, SecurityStamp newStamp) => account.WithOperator(Maybe<OperatorAttributes>.None, newStamp);
}

/// <summary>
/// Keeping platform access in line with the ERP contacts of a customer: contacts with portal access get a membership
/// (new people an account and an invitation), ERP memberships of people no longer listed are revoked.
/// </summary>
public static class ErpContactSync
{
    /// <summary>A contact with portal access, as validated by Access.</summary>
    public sealed record Contact(ErpContactId ExternalId, EmailAddress Email, PersonName Name, MembershipAttributes Attributes);

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
    public static IReadOnlyList<Step> Plan(CustomerCode customerCode, IReadOnlyList<Contact> contacts, IReadOnlyCollection<Member> existing, bool multiCustomer)
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
                .Where(m => m.CustomerCode == customerCode && m.Source is MembershipSource.Erp && !emails.Contains(member.Account.Email))
                .Select(m => new Step.Revoke(m, member.Account.Email)));

        return [.. additions, .. revocations];
    }

    public static Membership ErpMembership(Guid userId, CustomerCode customerCode, Contact contact) =>
        new(userId, customerCode, contact.Attributes, new MembershipSource.Erp(contact.ExternalId));
}
