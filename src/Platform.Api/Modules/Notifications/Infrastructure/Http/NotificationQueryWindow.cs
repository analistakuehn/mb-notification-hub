namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Http;

/// <summary>
/// Effective time window of a history read, echoed in the response so the
/// caller never has to guess which defaults applied.
/// </summary>
internal sealed record NotificationQueryWindow(DateTimeOffset From, DateTimeOffset To)
{
    /// <summary>Whether an instant falls inside the window, upper bound included.</summary>
    internal bool Contains(DateTimeOffset instant) => instant >= From && instant <= To;

    /// <summary>
    /// Applies the published defaults and refuses what the store cannot serve
    /// cheaply: the upper bound defaults to now, the lower bound to the
    /// default span before it, an inverted window is a bad request, and a span
    /// beyond the ceiling is refused instead of silently trimmed, because a
    /// silently trimmed window answers a question the caller did not ask.
    /// </summary>
    internal static bool TryResolve(
        DateTimeOffset? from,
        DateTimeOffset? to,
        DateTimeOffset now,
        out NotificationQueryWindow window,
        out string? error)
    {
        window = null!;
        error = null;

        DateTimeOffset upper = to ?? now;
        DateTimeOffset lower = from ?? upper - NotificationQueryContract.DefaultWindow;

        if (lower > upper)
        {
            error = "O início da janela precisa ser anterior ou igual ao fim.";
            return false;
        }

        if (upper - lower > NotificationQueryContract.MaxWindow)
        {
            error = $"A janela pedida excede o máximo de {NotificationQueryContract.MaxWindow.TotalDays:0} dias.";
            return false;
        }

        window = new NotificationQueryWindow(lower, upper);
        return true;
    }
}
