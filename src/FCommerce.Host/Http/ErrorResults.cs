using FCommerce.SharedKernel.Results;

namespace FCommerce.Host.Http;

/// <summary>Maps domain error values to RFC 9457 problem responses — the one place where errors meet HTTP.</summary>
internal static class ErrorResults
{
    public static IResult ToProblem(this Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return Results.Problem(
            statusCode: error.Kind switch
            {
                ErrorKind.Validation => StatusCodes.Status400BadRequest,
                ErrorKind.NotFound => StatusCodes.Status404NotFound,
                ErrorKind.Conflict => StatusCodes.Status409Conflict,
                ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status500InternalServerError,
            },
            title: error.Code,
            detail: error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });
    }

    public static IResult ToHttpResult<T>(this Result<T, Error> result, Func<T, IResult> onSuccess) =>
        result.Match(onSuccess, ToProblem);
}
