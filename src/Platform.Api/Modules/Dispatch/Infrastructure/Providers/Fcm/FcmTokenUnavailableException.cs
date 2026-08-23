namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Fcm;

/// <summary>
/// The OAuth token endpoint did not yield an access token. Transient by
/// definition: the send that needed the token reports a transient failure
/// and the queue redelivers, while misconfiguration (absent credentials)
/// stays an <see cref="InvalidOperationException"/> because no retry heals it.
/// </summary>
public sealed class FcmTokenUnavailableException : Exception
{
    public FcmTokenUnavailableException(string message)
        : base(message)
    {
    }

    public FcmTokenUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public FcmTokenUnavailableException()
    {
    }
}
