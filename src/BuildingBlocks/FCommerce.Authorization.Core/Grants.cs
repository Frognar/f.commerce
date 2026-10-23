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
        MembershipAttributes? current,
        MembershipAttributes requested,
        int otherAdministrators)
    {
        var allowed = grantor switch
        {
            Subject.Operator => true,
            Subject.CustomerUser user => user.ActiveCustomer == customerCode && user.Attributes.CanManageUsers,
            _ => false,
        };

        if (!allowed)
        {
            return Error.Forbidden("grant.not-allowed", "You cannot change memberships of this customer.");
        }

        var removesLastAdministrator = current is { CanManageUsers: true } && !requested.CanManageUsers && otherAdministrators == 0;
        return removesLastAdministrator
            ? Error.Conflict("grant.last-administrator", "The customer needs at least one user who can manage users.")
            : requested;
    }

    /// <summary>Only operators who manage operators change operator attributes; the last such operator keeps the right.</summary>
    public static Result<OperatorAttributes, Error> Operator(
        Subject grantor,
        OperatorAttributes? current,
        OperatorAttributes requested,
        int otherOperatorManagers)
    {
        if (grantor is not Subject.Operator { Attributes.CanManageOperators: true })
        {
            return Error.Forbidden("grant.not-allowed", "Only operators who manage operators can change operator rights.");
        }

        var removesLastManager = current is { CanManageOperators: true } && !requested.CanManageOperators && otherOperatorManagers == 0;
        return removesLastManager
            ? Error.Conflict("grant.last-operator-manager", "At least one operator must be able to manage operators.")
            : requested;
    }
}
