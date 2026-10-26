namespace FCommerce.Host.Api;

/// <summary>Answers shared by the endpoints of the public API.</summary>
internal static class ApiResults
{
    public static IResult Forbidden(string detail = "This key may not do that.") =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "api.forbidden", detail: detail, extensions: new Dictionary<string, object?> { ["code"] = "api.forbidden" });

    public static IResult BadRequest(string code, string detail, string field) =>
        Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: code,
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code, ["errors"] = new Dictionary<string, string[]> { [field] = [detail] } });
}
