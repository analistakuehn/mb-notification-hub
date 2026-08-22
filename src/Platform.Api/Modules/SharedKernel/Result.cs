namespace NotificationHub.SharedKernel;

public enum ResultErrorKind
{
    None = 0,
    Validation = 1,
    BusinessRule = 2,
    Integration = 3,
    NotFound = 4,
    Forbidden = 5,
}

public readonly record struct Result(bool IsSuccess, ResultErrorKind ErrorKind, string? Error)
{
    public bool IsFailure => !IsSuccess;

    public static Result Success() => new(true, ResultErrorKind.None, null);

    public static Result<T> Success<T>(T value) => new(true, value, ResultErrorKind.None, null);

    public static Result<T> ValidationError<T>(string error)
        => Failure<T>(ResultErrorKind.Validation, error);

    public static Result BusinessRuleViolation(string error)
        => Failure(ResultErrorKind.BusinessRule, error);

    public static Result<T> BusinessRuleViolation<T>(string error)
        => Failure<T>(ResultErrorKind.BusinessRule, error);

    public static Result<T> IntegrationFailure<T>(string error)
        => Failure<T>(ResultErrorKind.Integration, error);

    public static Result<T> NotFound<T>(string error)
        => Failure<T>(ResultErrorKind.NotFound, error);

    public static Result<T> Forbidden<T>(string error)
        => Failure<T>(ResultErrorKind.Forbidden, error);

    private static Result Failure(ResultErrorKind kind, string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return new Result(false, kind, error);
    }

    private static Result<T> Failure<T>(ResultErrorKind kind, string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return new Result<T>(false, default, kind, error);
    }
}

public readonly record struct Result<T>(bool IsSuccess, T? Value, ResultErrorKind ErrorKind, string? Error)
{
    public bool IsFailure => !IsSuccess;

    public TResult Match<TResult>(
        Func<T, TResult> onSuccess,
        Func<string, TResult> onValidationError,
        Func<string, TResult> onBusinessRuleViolation,
        Func<string, TResult> onIntegrationFailure,
        Func<string, TResult> onNotFound,
        Func<string, TResult> onForbidden)
    {
        if (IsSuccess)
        {
            return onSuccess(Value!);
        }

        var error = Error ?? "An unspecified failure occurred.";
        return ErrorKind switch
        {
            ResultErrorKind.Validation => onValidationError(error),
            ResultErrorKind.BusinessRule => onBusinessRuleViolation(error),
            ResultErrorKind.Integration => onIntegrationFailure(error),
            ResultErrorKind.NotFound => onNotFound(error),
            ResultErrorKind.Forbidden => onForbidden(error),
            _ => throw new InvalidOperationException($"Unsupported result error kind: {ErrorKind}."),
        };
    }

}
