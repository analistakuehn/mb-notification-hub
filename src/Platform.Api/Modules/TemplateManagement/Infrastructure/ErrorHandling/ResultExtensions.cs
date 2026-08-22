using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;

internal static class ResultExtensions
{
    /// <summary>Propagates a failure across result value types without touching the error.</summary>
    internal static Result<TTarget> AsFailure<TTarget>(this Result source)
        => new(false, default, source.ErrorKind, source.Error);

    /// <summary>Propagates a failure across result value types without touching the error.</summary>
    internal static Result<TTarget> AsFailure<TSource, TTarget>(this Result<TSource> source)
        => new(false, default, source.ErrorKind, source.Error);
}
