using System.Collections.Immutable;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Features;
using FCommerce.Testing;

namespace FCommerce.Authorization.Core.Tests;

public sealed class PolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly EmailAddress Anna = EmailAddress.Parse("anna@seller.pl").ShouldSucceed();
    private static readonly EmailAddress Piotr = EmailAddress.Parse("piotr@seller.pl").ShouldSucceed();

    private static readonly Subject Admin = new Subject.CustomerUser(Guid.NewGuid(), "C1", MembershipAttributes.Administrator);
    private static readonly Subject Buyer = new Subject.CustomerUser(Guid.NewGuid(), "C1", MembershipAttributes.Buyer);
    private static readonly Subject Accountant = new Subject.CustomerUser(Guid.NewGuid(), "C1", MembershipAttributes.Accounting);
    private static readonly Subject PlatformAdmin = new Subject.Operator(Guid.NewGuid(), Piotr, OperatorAttributes.PlatformAdministrator);
    private static readonly Subject SalespersonAnna = new Subject.Operator(Guid.NewGuid(), Anna, OperatorAttributes.Salesperson);

    private static AccessEnvironment Environment(CatalogVisibility visibility = CatalogVisibility.Public, bool customerActive = true, params FeatureId[] off) =>
        new(Now, visibility, [.. Feature.All.Select(f => f.Id).Except(off)], customerActive);

    private static Decision Decide(Subject subject, AccessAction action, Resource resource, AccessEnvironment? environment = null) =>
        PlatformPolicies.Decide(new AccessRequest(subject, action, resource, environment ?? Environment()));

    private static void AssertDenied(Decision decision, params string[] reasons) =>
        Assert.Equal(reasons, Assert.IsType<Decision.Denied>(decision).Reasons.Order());

    [Fact]
    public void Anything_not_explicitly_permitted_is_denied()
    {
        AssertDenied(Decide(Subject.Anonymous, AccessAction.Manage, new Resource.PlatformSettings()), "no-applicable-policy");
        AssertDenied(Decide(Admin, AccessAction.Read, new Resource.Customers()), "no-applicable-policy");
        AssertDenied(Decide(Subject.Anonymous, AccessAction.Read, new Resource.OwnAccount()), "sign-in-required");
    }

    [Theory]
    [InlineData(CatalogVisibility.Public, false, true)]
    [InlineData(CatalogVisibility.SignedInOnly, false, false)]
    [InlineData(CatalogVisibility.SignedInOnly, true, true)]
    public void Catalog_follows_its_visibility_setting(CatalogVisibility visibility, bool signedIn, bool permitted)
    {
        var decision = Decide(signedIn ? Buyer : Subject.Anonymous, AccessAction.Read, new Resource.Catalog(), Environment(visibility));

        Assert.Equal(permitted, decision.IsPermitted);
    }

    [Fact]
    public void Customer_users_act_only_for_their_active_customer()
    {
        Assert.True(Decide(Accountant, AccessAction.Download, new Resource.CustomerDocuments("C1", null)).IsPermitted);
        AssertDenied(Decide(Accountant, AccessAction.Download, new Resource.CustomerDocuments("C2", null)), "other-customer");
        AssertDenied(Decide(Admin, AccessAction.Manage, new Resource.CustomerUsers("C2", null)), "other-customer");
    }

    [Fact]
    public void Attributes_decide_within_the_customer()
    {
        AssertDenied(Decide(Buyer, AccessAction.Read, new Resource.CustomerDocuments("C1", null)), "cannot-view-documents");
        AssertDenied(Decide(Buyer, AccessAction.Manage, new Resource.CustomerUsers("C1", null)), "cannot-manage-users");
        Assert.True(Decide(Admin, AccessAction.Manage, new Resource.CustomerUsers("C1", null)).IsPermitted);
        Assert.True(Decide(Buyer, AccessAction.Read, new Resource.CustomerProfile("C1", null)).IsPermitted);
    }

    [Fact]
    public void An_inactive_customer_locks_out_its_users()
    {
        AssertDenied(Decide(Admin, AccessAction.Read, new Resource.CustomerProfile("C1", null), Environment(customerActive: false)), "customer-inactive");
    }

    [Fact]
    public void Switched_off_features_are_denied_to_everybody()
    {
        var documentsOff = Environment(off: Feature.Documents);

        AssertDenied(Decide(Accountant, AccessAction.Read, new Resource.CustomerDocuments("C1", null), documentsOff), "feature-off:documents");
        AssertDenied(Decide(PlatformAdmin, AccessAction.Read, new Resource.CustomerDocuments("C1", null), documentsOff), "feature-off:documents");
        AssertDenied(Decide(Subject.Anonymous, AccessAction.Submit, new Resource.Registrations(), Environment(off: Feature.SelfRegistration)), "feature-off:self-registration");
    }

    [Fact]
    public void Turning_off_customer_admin_invitations_leaves_user_management_to_operators()
    {
        var environment = Environment(off: Feature.CustomerAdminInvitations);

        AssertDenied(Decide(Admin, AccessAction.Manage, new Resource.CustomerUsers("C1", null), environment), "feature-off:customer-admin-invitations");
        Assert.True(Decide(Admin, AccessAction.Read, new Resource.CustomerUsers("C1", null), environment).IsPermitted);
        Assert.True(Decide(PlatformAdmin, AccessAction.Manage, new Resource.CustomerUsers("C1", null), environment).IsPermitted);
    }

    [Fact]
    public void Salesperson_operators_handle_only_their_own_customers()
    {
        Assert.True(Decide(SalespersonAnna, AccessAction.Manage, new Resource.CustomerUsers("C1", Anna)).IsPermitted);
        AssertDenied(Decide(SalespersonAnna, AccessAction.Manage, new Resource.CustomerUsers("C2", Piotr)), "outside-operator-scope");
        AssertDenied(Decide(SalespersonAnna, AccessAction.Read, new Resource.CustomerProfile("C3", null)), "outside-operator-scope");
        Assert.True(Decide(PlatformAdmin, AccessAction.Manage, new Resource.CustomerUsers("C2", Anna)).IsPermitted);
    }

    [Fact]
    public void Back_office_areas_require_their_operator_attribute()
    {
        Assert.True(Decide(PlatformAdmin, AccessAction.Manage, new Resource.PlatformSettings()).IsPermitted);
        AssertDenied(Decide(SalespersonAnna, AccessAction.Manage, new Resource.PlatformSettings()), "cannot-manage-settings");
        AssertDenied(Decide(SalespersonAnna, AccessAction.Manage, new Resource.Operators()), "cannot-manage-operators");
        Assert.True(Decide(SalespersonAnna, AccessAction.Read, new Resource.Customers()).IsPermitted);
        AssertDenied(Decide(Admin, AccessAction.Manage, new Resource.PlatformSettings()), "no-applicable-policy");
    }

    [Fact]
    public void Registrations_are_submitted_publicly_and_decided_by_operators()
    {
        var environment = Environment();

        Assert.True(Decide(Subject.Anonymous, AccessAction.Submit, new Resource.Registrations(), environment).IsPermitted);
        Assert.True(Decide(SalespersonAnna, AccessAction.Decide, new Resource.Registrations(), environment).IsPermitted);
        var noDecisions = new Subject.Operator(Guid.NewGuid(), Piotr, OperatorAttributes.Salesperson with { CanDecideRegistrations = false });
        AssertDenied(Decide(noDecisions, AccessAction.Decide, new Resource.Registrations(), environment), "cannot-decide-registrations");
        AssertDenied(Decide(Subject.Anonymous, AccessAction.Decide, new Resource.Registrations(), environment), "no-applicable-policy");
    }

    [Fact]
    public void Every_reason_for_a_denial_is_reported()
    {
        var decision = Decide(Buyer, AccessAction.Read, new Resource.CustomerDocuments("C2", null), Environment(off: Feature.Documents));

        AssertDenied(decision, "cannot-view-documents", "feature-off:documents", "other-customer");
    }
}

public sealed class GrantTests
{
    private static readonly Subject Admin = new Subject.CustomerUser(Guid.NewGuid(), "C1", MembershipAttributes.Administrator);
    private static readonly Subject Buyer = new Subject.CustomerUser(Guid.NewGuid(), "C1", MembershipAttributes.Buyer);
    private static readonly Subject Operator = new Subject.Operator(Guid.NewGuid(), EmailAddress.Parse("op@seller.pl").ShouldSucceed(), OperatorAttributes.PlatformAdministrator);

    [Fact]
    public void Customer_administrators_manage_memberships_of_their_own_customer_only()
    {
        Assert.Equal(MembershipAttributes.Buyer, Grants.Membership(Admin, "C1", null, MembershipAttributes.Buyer, 1).ShouldSucceed());
        Assert.Equal("grant.not-allowed", Grants.Membership(Admin, "C2", null, MembershipAttributes.Buyer, 1).ShouldFail().Code);
        Assert.Equal("grant.not-allowed", Grants.Membership(Buyer, "C1", null, MembershipAttributes.Buyer, 1).ShouldFail().Code);
        Assert.Equal("grant.not-allowed", Grants.Membership(Subject.Anonymous, "C1", null, MembershipAttributes.Buyer, 1).ShouldFail().Code);
    }

    [Fact]
    public void The_last_administrator_of_a_customer_keeps_user_management()
    {
        var demote = Grants.Membership(Operator, "C1", MembershipAttributes.Administrator, MembershipAttributes.Buyer, otherAdministrators: 0);
        var demoteWithAnother = Grants.Membership(Admin, "C1", MembershipAttributes.Administrator, MembershipAttributes.Buyer, otherAdministrators: 1);

        Assert.Equal("grant.last-administrator", demote.ShouldFail().Code);
        Assert.Equal(MembershipAttributes.Buyer, demoteWithAnother.ShouldSucceed());
    }

    [Fact]
    public void Only_operator_managers_change_operator_attributes_and_one_always_remains()
    {
        var salesperson = new Subject.Operator(Guid.NewGuid(), EmailAddress.Parse("s@seller.pl").ShouldSucceed(), OperatorAttributes.Salesperson);

        Assert.Equal(OperatorAttributes.Salesperson, Grants.Operator(Operator, null, OperatorAttributes.Salesperson, 0).ShouldSucceed());
        Assert.Equal("grant.not-allowed", Grants.Operator(salesperson, null, OperatorAttributes.Salesperson, 1).ShouldFail().Code);
        Assert.Equal("grant.not-allowed", Grants.Operator(Admin, null, OperatorAttributes.Salesperson, 1).ShouldFail().Code);
        Assert.Equal("grant.last-operator-manager", Grants.Operator(Operator, OperatorAttributes.PlatformAdministrator, OperatorAttributes.Salesperson, 0).ShouldFail().Code);
    }
}

public sealed class FeatureSetTests
{
    private static readonly ImmutableHashSet<FeatureId> Everything = [.. Feature.All.Select(f => f.Id)];

    [Fact]
    public void A_feature_is_on_only_when_available_enabled_and_its_requirements_are_on()
    {
        var set = new FeatureSet(Everything, [Feature.CustomerAccounts, Feature.Documents]);

        Assert.True(set.IsOn(Feature.Documents));
        Assert.False(set.IsOn(Feature.SelfRegistration));
        Assert.False((set with { Enabled = [Feature.Documents] }).IsOn(Feature.Documents)); // requirement switched off
        Assert.False((set with { Available = [Feature.CustomerAccounts] }).IsOn(Feature.Documents)); // not available here
    }

    [Fact]
    public void Enabling_validates_availability_and_requirements()
    {
        var installation = new FeatureSet(Everything.Remove(Feature.SelfRegistration), []);

        Assert.Equal("feature.unavailable", installation.WithEnabled([Feature.CustomerAccounts, Feature.SelfRegistration]).ShouldFail().Code);
        Assert.Equal("feature.missing-requirement", installation.WithEnabled([Feature.Documents]).ShouldFail().Code);
        Assert.True(installation.WithEnabled([Feature.CustomerAccounts, Feature.Documents]).ShouldSucceed().IsOn(Feature.Documents));
    }

    [Fact]
    public void Feature_ids_parse_only_from_the_catalog()
    {
        Assert.Equal(Feature.Documents, FeatureId.Parse("documents").ShouldSucceed());
        Assert.Equal("feature.unknown", FeatureId.Parse("teleport").ShouldFail().Code);
    }
}
