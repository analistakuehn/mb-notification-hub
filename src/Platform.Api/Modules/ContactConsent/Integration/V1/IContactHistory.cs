using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.ContactConsent.Integration.V1;

/// <summary>
/// In-process read surface of this module for an evidence composer. It is a
/// contract of its own, not an extension of <see cref="IRecipientDirectory"/>,
/// for two reasons: the lifecycle stamps it carries must not reach the support
/// query surface, and a disclosure call site must be distinct and searchable
/// from an operational one.
/// </summary>
/// <remarks>
/// Not part of this version: revealing the plaintext value of a contact point
/// for audit, including one already removed. That disclosure is a decision this
/// module cannot make on its own, and until it is taken the value member is
/// absent rather than empty.
/// </remarks>
public interface IContactHistory
{
    /// <summary>
    /// Describes a set of contact points of one recipient, active or removed.
    /// Unknown ids and ids of another recipient are simply absent from the
    /// answer, which keeps the read from confirming the existence of anything.
    /// </summary>
    Task<Result<IReadOnlyList<HistoricalContactPoint>>> DescribeContactPointsAsync(
        string recipientId,
        IReadOnlyCollection<Guid> contactPointIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Describes a set of device registrations of one recipient, active or
    /// invalidated, with no token in any form.
    /// </summary>
    Task<Result<IReadOnlyList<HistoricalDeviceRegistration>>> DescribeDeviceRegistrationsAsync(
        string recipientId,
        IReadOnlyCollection<Guid> deviceTokenIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads every consent entry of one recipient recorded inside the window,
    /// oldest first. The window is mandatory because the ledger grows without
    /// bound and an unbounded read would scan it whole.
    /// </summary>
    Task<Result<IReadOnlyList<ConsentLedgerEntry>>> ReadConsentLedgerAsync(
        string recipientId,
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        CancellationToken cancellationToken);
}
