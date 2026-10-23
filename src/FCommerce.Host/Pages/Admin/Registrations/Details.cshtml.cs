using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Authorization.Core;
using FCommerce.Customers.Contracts;
using FCommerce.Host.Http;
using FCommerce.Host.Localization;
using FCommerce.Host.Security;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace FCommerce.Host.Pages.Admin.Registrations;

/// <summary>One request: the ERP customers with the same tax id as hints, approve (link to a customer) or reject.</summary>
[RequiresFeature("self-registration")]
public sealed class DetailsModel(
    IQueryHandler<GetRegistrationRequest, Result<RegistrationDetails, Error>> requests,
    IQueryHandler<FindCustomersByTaxId, Result<IReadOnlyList<CustomerSummary>, Error>> byTaxId,
    IQueryHandler<GetCustomer, Result<CustomerDetails, Error>> customers,
    ICommandHandler<ApproveRegistration, Result<Unit, Error>> approve,
    ICommandHandler<RejectRegistration, Result<Unit, Error>> reject,
    AccessControl access,
    CurrentUser user,
    IStringLocalizer<SharedResource> texts) : PageModel
{
    public RegistrationDetails? Registration { get; private set; }

    /// <summary>ERP customers with the applicant's tax id.</summary>
    public IReadOnlyList<CustomerSummary> Matches { get; private set; } = [];

    public string? Saved { get; private set; }

    [BindProperty]
    public string CustomerCode { get; set; } = string.Empty;

    [BindProperty]
    public string Template { get; set; } = "administrator";

    [BindProperty]
    public string Reason { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        await LoadAsync(id, cancellationToken);
        if (Registration is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostApproveAsync(Guid id, CancellationToken cancellationToken)
    {
        // The chosen customer must be within the operator's scope (a salesperson links only to their own customers).
        var customer = (await customers.HandleAsync(new GetCustomer(CustomerCode), cancellationToken)).Match(c => c, _ => (CustomerDetails?)null);
        var salesperson = customer?.Salesperson?.Email is { } email ? EmailAddress.Parse(email).Match(e => e, _ => (EmailAddress?)null) : null;
        if (customer is not null && !await access.CanAsync(AccessAction.Manage, new Resource.CustomerUsers(customer.Code, salesperson), cancellationToken))
        {
            return Forbid();
        }

        return await ExecuteAsync(id, () => approve.HandleAsync(new ApproveRegistration(user.UserId!.Value, id, CustomerCode, Template), cancellationToken), "RegistrationApproved", cancellationToken);
    }

    public Task<IActionResult> OnPostRejectAsync(Guid id, CancellationToken cancellationToken) =>
        ExecuteAsync(id, () => reject.HandleAsync(new RejectRegistration(user.UserId!.Value, id, Reason), cancellationToken), "RegistrationRejected", cancellationToken);

    private async Task<IActionResult> ExecuteAsync(Guid id, Func<Task<Result<Unit, Error>>> command, string savedText, CancellationToken cancellationToken)
    {
        (await command()).Switch(
            _ =>
            {
                Saved = texts[savedText];
                ModelState.Clear();
            },
            error => ModelState.AddErrors(error, texts));
        await LoadAsync(id, cancellationToken);
        return Page();
    }

    private async Task LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        Registration = (await requests.HandleAsync(new GetRegistrationRequest(id), cancellationToken)).Match(r => r, _ => (RegistrationDetails?)null);
        if (Registration is { Status: "pending" })
        {
            Matches = (await byTaxId.HandleAsync(new FindCustomersByTaxId(Registration.TaxId), cancellationToken)).Match(found => found, _ => []);
            if (string.IsNullOrEmpty(CustomerCode) && Matches is [var only])
            {
                CustomerCode = only.Code;
            }
        }
    }
}
