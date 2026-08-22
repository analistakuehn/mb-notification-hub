using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// Structured details recovered from a formatted error string:
/// code, human detail and, for state-transition conflicts, the current status
/// with the transitions the lifecycle allows from it.
/// </summary>
public sealed record DomainErrorInfo(
    string Code,
    string Detail,
    string? CurrentStatus,
    IReadOnlyList<string> AllowedTransitions);

/// <summary>
/// Encodes structured error data into the single error string the shared
/// <see cref="Result"/> type carries, and decodes it back at the HTTP boundary.
/// Fields are separated by the unit separator control character, which cannot
/// appear in validated user input.
/// </summary>
public static class DomainError
{
    private const char FieldSeparator = (char)0x1F;

    public static string Format(string code, string detail)
        => $"{code}{FieldSeparator}{detail}";

    public static string StateTransition(string currentStatus, IReadOnlyList<string> allowedTransitions, string detail)
        => $"{ErrorCodes.InvalidStateTransition}{FieldSeparator}{detail}{FieldSeparator}{currentStatus}{FieldSeparator}{string.Join(',', allowedTransitions)}";

    public static DomainErrorInfo Describe(string? error, ResultErrorKind kind)
    {
        if (string.IsNullOrEmpty(error))
        {
            return new DomainErrorInfo(FallbackCode(kind), "The request could not be completed.", null, []);
        }

        var fields = error.Split(FieldSeparator);
        if (fields.Length == 1)
        {
            return new DomainErrorInfo(FallbackCode(kind), error, null, []);
        }

        var code = fields[0];
        var detail = fields[1];
        if (fields.Length < 4 || !string.Equals(code, ErrorCodes.InvalidStateTransition, StringComparison.Ordinal))
        {
            return new DomainErrorInfo(code, detail, null, []);
        }

        var allowed = fields[3].Length == 0 ? [] : fields[3].Split(',');
        return new DomainErrorInfo(code, detail, fields[2], allowed);
    }

    private static string FallbackCode(ResultErrorKind kind) => kind switch
    {
        ResultErrorKind.Validation => ErrorCodes.InvalidRequest,
        ResultErrorKind.NotFound => "not-found",
        ResultErrorKind.Forbidden => "forbidden",
        ResultErrorKind.BusinessRule => "conflict",
        _ => "request-failed",
    };
}
