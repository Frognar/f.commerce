using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Authorization.Core;

/// <summary>Rules for changing attributes (who may give whom what), separate from using them.</summary>
public static class Grants
{
    /// <summary>
    /// Whether <paramref name="grantor"/> may set a membership of <paramref name="customerCode"/> to
    /// <paramref name="requested"/>. <paramref name="otherAdministrators"/> counts the customer's other members
    /// with <see cref="MembershipAttributes.CanManageUsers"/>: the last one cannot lose it (the company would be locked out).
    /// </summary>
    public static Result<MembershipAttributes, Error> Membership(
        Subject grantor,
        string customerCode,
        Maybe<MembershipAttributes> current,
        MembershipAttributes requested,
        int otherAdministrators) =>
        MayManage(grantor, customerCode)
            .Bind(_ => KeepsAnAdministrator(current, requested.CanManageUsers, otherAdministrators))
            .Map(_ => requested);

    /// <summary>Whether <paramref name="grantor"/> may take a membership away; the last administrator keeps theirs.</summary>
    public static Result<Unit, Error> MembershipRevocation(Subject grantor, string customerCode, MembershipAttributes current, int otherAdministrators) =>
        MayManage(grantor, customerCode).Bind(_ => KeepsAnAdministrator(current, keepsRight: false, otherAdministrators));

    /// <summary>Only operators who manage operators change operator attributes; the last such operator keeps the right.</summary>
    public static Result<OperatorAttributes, Error> Operator(
        Subject grantor,
        Maybe<OperatorAttributes> current,
        OperatorAttributes requested,
        int otherOperatorManagers) =>
        MayManageOperators(grantor)
            .Bind(_ => KeepsAnOperatorManager(current, requested.CanManageOperators, otherOperatorManagers))
            .Map(_ => requested);

    /// <summary>Whether <paramref name="grantor"/> may take operator rights away; the last operator manager keeps theirs.</summary>
    public static Result<Unit, Error> OperatorRevocation(Subject grantor, OperatorAttributes current, int otherOperatorManagers) =>
        MayManageOperators(grantor).Bind(_ => KeepsAnOperatorManager(current, keepsRight: false, otherOperatorManagers));

    private static Result<Unit, Error> MayManage(Subject grantor, string customerCode) =>
        grantor switch
        {
            Subject.Operator => Unit.Value,
            Subject.CustomerUser user when user.ActiveCustomer == customerCode && user.Attributes.CanManageUsers => Unit.Value,
            _ => Error.Forbidden("grant.not-allowed", "You cannot change memberships of this customer."),
        };

    private static Result<Unit, Error> KeepsAnAdministrator(Maybe<MembershipAttributes> current, bool keepsRight, int otherAdministrators) =>
        current.Exists(c => c.CanManageUsers) && !keepsRight && otherAdministrators == 0
            ? Error.Conflict("grant.last-administrator", "The customer needs at least one user who can manage users.")
            : Unit.Value;

    private static Result<Unit, Error> MayManageOperators(Subject grantor) =>
        grantor is Subject.Operator { Attributes.CanManageOperators: true }
            ? Unit.Value
            : Error.Forbidden("grant.not-allowed", "Only operators who manage operators can change operator rights.");

    private static Result<Unit, Error> KeepsAnOperatorManager(Maybe<OperatorAttributes> current, bool keepsRight, int otherOperatorManagers) =>
        current.Exists(c => c.CanManageOperators) && !keepsRight && otherOperatorManagers == 0
            ? Error.Conflict("grant.last-operator-manager", "At least one operator must be able to manage operators.")
            : Unit.Value;
}
