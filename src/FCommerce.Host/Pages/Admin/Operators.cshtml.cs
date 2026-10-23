using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Host.Http;
using FCommerce.Host.Localization;
using FCommerce.Host.Security;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace FCommerce.Host.Pages.Admin;

public sealed class OperatorsModel(
    CurrentUser user,
    IQueryHandler<GetOperators, IReadOnlyList<OperatorSummary>> operators,
    ICommandHandler<InviteOperator, Result<Guid, Error>> invite,
    ICommandHandler<ChangeOperator, Result<Unit, Error>> change,
    ICommandHandler<RevokeOperator, Result<Unit, Error>> revoke,
    IStringLocalizer<SharedResource> texts) : PageModel
{
    public sealed class OperatorForm
    {
        public string Email { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Culture { get; set; } = string.Empty;

        public bool CanManageSettings { get; set; }

        public bool CanManageOperators { get; set; }

        public bool CanDecideRegistrations { get; set; }

        public string CustomerScope { get; set; } = "as-salesperson";

        public OperatorView ToView() => new(CanManageSettings, CanManageOperators, CanDecideRegistrations, CustomerScope);
    }

    [BindProperty]
    public OperatorForm Form { get; set; } = new();

    public IReadOnlyList<OperatorSummary> Operators { get; private set; } = [];

    public string? Saved { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Operators = await operators.HandleAsync(new GetOperators(), cancellationToken);

    public Task<IActionResult> OnPostInviteAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(() => invite.HandleAsync(new InviteOperator(user.UserId!.Value, Form.Email, Form.Name, Form.Culture, Form.ToView()), cancellationToken), "OperatorInvited", cancellationToken);

    public Task<IActionResult> OnPostChangeAsync(Guid id, CancellationToken cancellationToken) =>
        ExecuteAsync(() => change.HandleAsync(new ChangeOperator(user.UserId!.Value, id, Form.ToView()), cancellationToken), "Saved", cancellationToken);

    public Task<IActionResult> OnPostRevokeAsync(Guid id, CancellationToken cancellationToken) =>
        ExecuteAsync(() => revoke.HandleAsync(new RevokeOperator(user.UserId!.Value, id), cancellationToken), "OperatorRevoked", cancellationToken);

    private async Task<IActionResult> ExecuteAsync<T>(Func<Task<Result<T, Error>>> command, string savedText, CancellationToken cancellationToken)
    {
        (await command()).Switch(_ => Saved = texts[savedText], error => ModelState.AddErrors(error, texts));
        if (Saved is not null)
        {
            ModelState.Clear();
            Form = new OperatorForm();
        }

        await OnGetAsync(cancellationToken);
        return Page();
    }
}
