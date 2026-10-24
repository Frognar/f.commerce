using FCommerce.Access.Domain;
using FCommerce.Testing;

namespace FCommerce.Access.Tests;

public sealed class RegistrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static UnvalidatedRegistration Input() =>
        new(" Nowa Firma sp. z o.o. ", "526-104-08-28", "ul. Długa 1", "00950", "Warszawa", "pl", "Jan Kowalski", "Jan@Firma.PL", default, "pl", AcceptedTerms: true);

    private static RegistrationRequest Pending()
    {
        var (company, applicant) = Registrations.Validate(Input()).ShouldBeValid();
        return Registrations.Submit(Guid.NewGuid(), company, applicant, Now);
    }

    [Fact]
    public void A_valid_form_is_normalized()
    {
        var (company, applicant) = Registrations.Validate(Input()).ShouldBeValid();

        Assert.Equal(("Nowa Firma sp. z o.o.", "PL5261040828", "00-950", "PL"), (company.Name, company.TaxId.Value, company.Address.PostalCode.Value, company.Address.Country.Value));
        Assert.Equal("jan@firma.pl", applicant.Email.Value);
    }

    [Fact]
    public void Every_problem_is_reported_with_its_field()
    {
        var input = Input() with { CompanyName = " ", TaxId = "5261040827", PostalCode = "1234", Email = "jan@", AcceptedTerms = false };

        var fields = Registrations.Validate(input).ShouldBeInvalid().Select(e => e.Field);

        Assert.Equal(["companyName", "taxId", "postalCode", "email", "acceptedTerms"], fields);
    }

    [Fact]
    public void A_request_is_decided_once()
    {
        var operatorId = Guid.NewGuid();
        var approved = Registrations.Approve(Pending(), operatorId, "K-001", Now).ShouldSucceed();

        Assert.Equal(new RegistrationStatus.Approved(operatorId, Now, "K-001"), approved.Status);
        Assert.Equal("registration.already-decided", Registrations.Approve(approved, operatorId, "K-002", Now).ShouldFail().Code);
        Assert.Equal("registration.already-decided", Registrations.Reject(approved, operatorId, "Duplicate", Now).ShouldFail().Code);
    }

    [Fact]
    public void A_rejection_needs_a_reason()
    {
        var error = Registrations.Reject(Pending(), Guid.NewGuid(), "  ", Now).ShouldFail();
        var rejected = Registrations.Reject(Pending(), Guid.NewGuid(), " Not a business customer. ", Now).ShouldSucceed();

        Assert.Equal(("reason.empty", "reason"), (error.Code, error.Field));
        Assert.Equal("Not a business customer.", Assert.IsType<RegistrationStatus.Rejected>(rejected.Status).Reason);
    }
}
