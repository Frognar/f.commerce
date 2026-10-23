using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Host.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FCommerce.Host.Pages.Admin.Registrations;

[RequiresFeature("self-registration")]
public sealed class IndexModel(IQueryHandler<GetRegistrationRequests, IReadOnlyList<RegistrationSummary>> requests) : PageModel
{
    public static readonly string[] Statuses = ["pending", "approved", "rejected"];

    [BindProperty(SupportsGet = true, Name = "status")]
    public string Status { get; set; } = "pending";

    public IReadOnlyList<RegistrationSummary> Requests { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Status = Statuses.Contains(Status) ? Status : "pending";
        Requests = await requests.HandleAsync(new GetRegistrationRequests(Status), cancellationToken);
    }
}
