namespace FCommerce.Host.Http;

internal static class HtmxRequest
{
    /// <summary>True for any request made by htmx.</summary>
    public static bool IsHtmx(this HttpRequest request) => request.Headers["HX-Request"] == "true";

    /// <summary>True when htmx requested a fragment for the element with the given id.</summary>
    public static bool IsHtmxFor(this HttpRequest request, string targetId) =>
        request.Headers["HX-Request"] == "true" && request.Headers["HX-Target"] == targetId;
}
