namespace NotificationHub.Api.Modules.Notifications.Features.Pipeline;

/// <summary>
/// Explicit result of one pipeline stage. Rejection and deferral are valid
/// business outcomes carried as data; an unexpected failure propagates as an
/// exception so the message returns to the queue instead of contaminating the
/// audited result.
/// </summary>
public enum StageOutcome
{
    Continue,
    Reject,
    Defer,
}

/// <summary>
/// One ordered stage of the Core pipeline. A stage reads what earlier stages
/// filled into the context, fills its own slice, and reports its outcome; the
/// order is composed at startup, never discovered by reflection.
/// </summary>
public interface INotificationStage
{
    /// <summary>Stable stage name recorded in the trace of every run.</summary>
    string Name { get; }

    Task<StageOutcome> ExecuteAsync(NotificationContext context, CancellationToken cancellationToken);
}
