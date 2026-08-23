using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Reads;

/// <summary>
/// What the query surface may say about where a delivery attempt was aimed:
/// the masked contact points, computed by their owning module, and the push
/// platform of each device registration. Never a contact value, never a
/// routing token.
/// </summary>
/// <param name="ContactPoints">Masked contact points, keyed by their identity.</param>
/// <param name="DevicePlatforms">Push platform of each device registration still active.</param>
/// <param name="DeviceRegistrationsAnswered">
/// Whether the directory answered the device read at all. It separates two
/// facts a missing platform would otherwise collapse: a registration the
/// directory says is no longer active, and a directory that could not answer.
/// The identity in the attempt came from this recipient's own fan-out, so an
/// answered read that omits it is conclusive.
/// </param>
internal sealed record AttemptTargets(
    IReadOnlyDictionary<Guid, MaskedContactPoint> ContactPoints,
    IReadOnlyDictionary<Guid, string> DevicePlatforms,
    bool DeviceRegistrationsAnswered)
{
    internal static AttemptTargets Empty { get; } = new(
        new Dictionary<Guid, MaskedContactPoint>(),
        new Dictionary<Guid, string>(),
        DeviceRegistrationsAnswered: false);
}

/// <summary>
/// Resolves the targets of a notification's attempts through the published
/// contract of the contact directory, in one read per kind. The masking runs
/// inside the owning module, so this context never holds a contact value and
/// could not leak one.
/// </summary>
/// <remarks>
/// Enrichment degrades instead of failing: a directory that cannot answer
/// leaves the identity of the target in the response and drops the masked
/// value, because a support agent reading the history is better served by a
/// partial answer than by an error.
/// </remarks>
internal sealed class AttemptTargetDirectory(
    IRecipientDirectory directory,
    ILogger<AttemptTargetDirectory> logger)
{
    internal async Task<AttemptTargets> ResolveAsync(
        string recipientId,
        IReadOnlyCollection<Guid> contactPointIds,
        IReadOnlyCollection<Guid> deviceTokenIds,
        CancellationToken cancellationToken)
    {
        if (contactPointIds.Count == 0 && deviceTokenIds.Count == 0)
        {
            return AttemptTargets.Empty;
        }

        Dictionary<Guid, MaskedContactPoint> contactPoints = [];
        Dictionary<Guid, string> devicePlatforms = [];
        var devicesAnswered = false;

        if (contactPointIds.Count > 0)
        {
            try
            {
                Result<IReadOnlyList<MaskedContactPoint>> masked =
                    await directory.MaskContactPointsAsync(recipientId, contactPointIds, cancellationToken);
                if (masked.IsSuccess)
                {
                    contactPoints = masked.Value!.ToDictionary(point => point.ContactPointId);
                }
                else
                {
                    logger.ContactTargetsUnavailable(recipientId, masked.Error ?? string.Empty);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.ContactTargetsFailed(recipientId, exception);
            }
        }

        if (deviceTokenIds.Count > 0)
        {
            try
            {
                // The snapshot only carries active registrations. An answered
                // read that omits an identity therefore states that the
                // registration is no longer active, which is a different fact
                // from a read that never answered.
                Result<RecipientSnapshot> snapshot =
                    await directory.FindAsync(recipientId, cancellationToken);
                if (snapshot.IsSuccess)
                {
                    devicesAnswered = true;
                    devicePlatforms = snapshot.Value!.Devices
                        .Where(device => deviceTokenIds.Contains(device.DeviceTokenId))
                        .ToDictionary(device => device.DeviceTokenId, device => device.Platform);
                }
                else
                {
                    logger.DeviceTargetsUnavailable(recipientId, snapshot.Error ?? string.Empty);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.DeviceTargetsFailed(recipientId, exception);
            }
        }

        return new AttemptTargets(contactPoints, devicePlatforms, devicesAnswered);
    }
}
