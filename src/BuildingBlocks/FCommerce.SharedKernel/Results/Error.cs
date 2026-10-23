namespace FCommerce.SharedKernel.Results;

public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
    Forbidden,
}

/// <summary>An expected failure, returned as a value instead of thrown.</summary>
public sealed record Error(ErrorKind Kind, string Code, string Message)
{
    public static Error Validation(string code, string message) => new(ErrorKind.Validation, code, message);

    public static Error NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);

    public static Error Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);

    public static Error Forbidden(string code, string message) => new(ErrorKind.Forbidden, code, message);
}
