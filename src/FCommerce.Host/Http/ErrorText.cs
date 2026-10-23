using FCommerce.Host.Localization;
using FCommerce.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Localization;

namespace FCommerce.Host.Http;

/// <summary>Shows domain errors to people: a translated text per error code ("Error.{code}"), the English message otherwise.</summary>
internal static class ErrorText
{
    public static string Describe(this IStringLocalizer<SharedResource> texts, Error error)
    {
        var text = texts[$"Error.{error.Code}"];
        return text.ResourceNotFound ? error.Message : text.Value;
    }

    /// <summary>Adds the error (or each collected error) to the model state, under its field when it has one.</summary>
    public static void AddErrors(this ModelStateDictionary modelState, Error error, IStringLocalizer<SharedResource> texts)
    {
        foreach (var single in error.Details.IsEmpty ? [error] : error.Details.AsImmutableArray().AsEnumerable())
        {
            modelState.AddModelError(single.Field ?? string.Empty, texts.Describe(single));
        }
    }
}
