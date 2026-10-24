using System.Globalization;
using FCommerce.Access.Contracts;
using FCommerce.Application.Messaging;
using FCommerce.Authorization.Core;
using FCommerce.Host.Http;
using FCommerce.Host.Localization;
using FCommerce.Host.Security;
using FCommerce.SharedKernel.Optional;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;

namespace FCommerce.Host.Pages.Auth;

/// <summary>A company applies for an account; does not exist without the "self-registration" feature.</summary>
[RequiresFeature("self-registration")]
[EnableRateLimiting(RateLimits.Authentication)]
public sealed class RegisterModel(
    ICommandHandler<SubmitRegistration, Result<Guid, Error>> submit,
    AccessControl access,
    IStringLocalizer<SharedResource> texts) : PageModel
{
    /// <summary>The form as posted (optional fields arrive empty or missing).</summary>
    [NullableEdge]
    public sealed class RegistrationForm
    {
        public string CompanyName { get; set; } = string.Empty;

        public string TaxId { get; set; } = string.Empty;

        public string Street { get; set; } = string.Empty;

        public string PostalCode { get; set; } = string.Empty;

        public string City { get; set; } = string.Empty;

        public string Country { get; set; } = "PL";

        public string PersonName { get; set; } = string.Empty;

        public string Email { get; set; } = string.Empty;

        public string? Phone { get; set; }

        public bool AcceptedTerms { get; set; }

        /// <summary>Honeypot: hidden from people, filled in by bots.</summary>
        public string? Website { get; set; }
    }

    [BindProperty]
    public RegistrationForm Form { get; set; } = new();

    public bool Submitted { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) =>
        await access.CanAsync(AccessAction.Submit, new Resource.Registrations(), cancellationToken) ? Page() : Forbid();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!await access.CanAsync(AccessAction.Submit, new Resource.Registrations(), cancellationToken))
        {
            return Forbid();
        }

        if (!string.IsNullOrEmpty(Form.Website))
        {
            Submitted = true; // a bot: pretend success, store nothing
            return Page();
        }

        var result = await submit.HandleAsync(
            new SubmitRegistration(
                Form.CompanyName, Form.TaxId, Form.Street, Form.PostalCode, Form.City, Form.Country,
                Form.PersonName, Form.Email, Form.Phone, CultureInfo.CurrentUICulture.Name, Form.AcceptedTerms),
            cancellationToken);
        result.Switch(_ => Submitted = true, error => ModelState.AddErrors(error, texts));
        return Page();
    }
}
