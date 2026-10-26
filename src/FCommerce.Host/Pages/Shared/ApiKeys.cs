using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Application.Results;
using FCommerce.Host.Http;
using FCommerce.Host.Localization;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Localization;

namespace FCommerce.Host.Pages.Shared;

/// <summary>The form that creates an API key (customer area).</summary>
public sealed class ApiKeyForm
{
    public string Name { get; set; } = string.Empty;

    public Guid OwnerId { get; set; }

    /// <summary>"30", "90" or "365"; empty: the key does not expire.</summary>
    public string ValidForDays { get; set; } = string.Empty;
}

/// <summary>What the API keys panel shows.</summary>
/// <param name="Owners">Users a key can be issued for (active ones); empty when the panel is read-only.</param>
/// <param name="Created">The key just created, shown once.</param>
public sealed record ApiKeysPanel(
    IReadOnlyList<ApiKeySummary> Keys,
    bool CanManage,
    bool CanCreate,
    IReadOnlyList<CustomerUserSummary> Owners,
    Maybe<CreatedApiKey> Created,
    IDictionary<string, string> RouteValues,
    Maybe<Guid> CurrentUserId);

/// <summary>Runs the API key commands for the pages that host the panel.</summary>
public sealed class ApiKeyActions(
    ICommandHandler<CreateApiKey, Result<CreatedApiKey, Error>> create,
    ICommandHandler<RevokeApiKey, Result<Unit, Error>> revoke,
    IQueryHandler<GetCustomerApiKeys, IReadOnlyList<ApiKeySummary>> keys,
    IStringLocalizer<SharedResource> texts)
{
    public Task<IReadOnlyList<ApiKeySummary>> KeysAsync(string customerCode, CancellationToken cancellationToken) =>
        keys.HandleAsync(new GetCustomerApiKeys(customerCode), cancellationToken);

    /// <summary>Creates the key; none after adding the errors to the model state.</summary>
    public async Task<Maybe<CreatedApiKey>> CreateAsync(Guid actorId, string customerCode, ApiKeyForm form, ModelStateDictionary modelState, CancellationToken cancellationToken)
    {
        var days = form.ValidForDays.Length == 0
            ? Result.Ok<Maybe<int>, Error>(Maybe<int>.None)
            : FormNumbers.Whole(form.ValidForDays, "api-key.validity-invalid", "validForDays").Map(Maybe.Some);
        var created = await days.BindAsync(validFor =>
            create.HandleAsync(new CreateApiKey(actorId, form.OwnerId, customerCode, form.Name.OrEmpty(), validFor.OrNull()), cancellationToken));
        return created.Match(
            key =>
            {
                modelState.Clear();
                return Maybe.Some(key);
            },
            error =>
            {
                modelState.AddErrors(error, texts);
                return Maybe<CreatedApiKey>.None;
            });
    }

    /// <summary>Revokes the key; returns the confirmation text, or none after adding the errors to the model state.</summary>
    public async Task<Maybe<string>> RevokeAsync(Guid actorId, string customerCode, Guid keyId, ModelStateDictionary modelState, CancellationToken cancellationToken) =>
        (await revoke.HandleAsync(new RevokeApiKey(actorId, customerCode, keyId), cancellationToken)).Match(
            _ =>
            {
                modelState.Clear();
                return Maybe.Some<string>(texts["ApiKeyRevoked"]);
            },
            error =>
            {
                modelState.AddErrors(error, texts);
                return Maybe<string>.None;
            });
}
