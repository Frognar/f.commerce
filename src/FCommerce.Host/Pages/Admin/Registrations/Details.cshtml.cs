using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Authorization.Core;
using FCommerce.Customers.Contracts;
using FCommerce.Host.Http;
using FCommerce.Host.Localization;
using FCommerce.Host.Security;
using FCommerce.SharedKernel.Contact;
using FCommerce.SharedKernel.Optional;
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
    private Maybe<RegistrationDetails> registration;

    public bool Found => registration.Match(_ => true, () => false);

    /// <summary>The request; read only when <see cref="Found"/>.</summary>
    public RegistrationDetails Registration => registration.Match(r => r, () => throw new InvalidOperationException("The request is not loaded."));

    /// <summary>ERP customers with the applicant's tax id.</summary>
    public IReadOnlyList<CustomerSummary> Matches { get; private set; } = [];

    public Maybe<string> Saved { get; private set; }

    [BindProperty]
    public string CustomerCode { get; set; } = string.Empty;

    [BindProperty]
    public string Template { get; set; } = "administrator";

    [BindProperty]
    public string Reason { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        await LoadAsync(id, cancellationToken);
        if (!Found)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostApproveAsync(Guid id, CancellationToken cancellationToken)
    {
        // The chosen customer must be within the operator's scope (a salesperson links only to their own customers).
        var customer = (await customers.HandleAsync(new GetCustomer(CustomerCode), cancellationToken)).ToMaybe();
        var allowed = await customer.Match(
            c => access.CanAsync(AccessAction.Manage, new Resource.CustomerUsers(c.Code, Company.CompanyContext.Salesperson(c)), cancellationToken),
            () => Task.FromResult(true)); // an unknown customer is reported by the approval itself
        if (!allowed)
        {
            return Forbid();
        }

        return await ExecuteAsync(id, () => approve.HandleAsync(new ApproveRegistration(user.RequiredUserId, id, CustomerCode, Template), cancellationToken), "RegistrationApproved", cancellationToken);
    }

    public Task<IActionResult> OnPostRejectAsync(Guid id, CancellationToken cancellationToken) =>
        ExecuteAsync(id, () => reject.HandleAsync(new RejectRegistration(user.RequiredUserId, id, Reason), cancellationToken), "RegistrationRejected", cancellationToken);

    private async Task<IActionResult> ExecuteAsync(Guid id, Func<Task<Result<Unit, Error>>> command, string savedText, CancellationToken cancellationToken)
    {
        (await command()).Switch(
            _ =>
            {
                Saved = Maybe.Some<string>(texts[savedText]);
                ModelState.Clear();
            },
            error => ModelState.AddErrors(error, texts));
        await LoadAsync(id, cancellationToken);
        return Page();
    }

    private async Task LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        registration = (await requests.HandleAsync(new GetRegistrationRequest(id), cancellationToken)).ToMaybe();
        foreach (var pending in registration.Where(r => r.Status == "pending").AsEnumerable())
        {
            Matches = (await byTaxId.HandleAsync(new FindCustomersByTaxId(pending.TaxId), cancellationToken)).Match(found => found, _ => []);
            if (string.IsNullOrEmpty(CustomerCode) && Matches is [var only])
            {
                CustomerCode = only.Code;
            }
        }
    }
}
