using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;

namespace NotificationHub.IntegrationTests.Dispatch;

/// <summary>
/// A stand-in for the module that owns the proof of the bytes, keeping every
/// submission it was handed.
/// <para>
/// It answers the verdict of a witness nobody produced unless a test says
/// otherwise, which is the same answer the vocabulary gives a stand-in that
/// was never told what to say. What it is here for is the other direction: the
/// measurements a send hands over are the only observable a suite has of what
/// the adapter actually measured, and a settlement that never arrived is
/// exactly as visible as one that did.
/// </para>
/// </summary>
public sealed class AttachmentSubmissionWitnessDouble : IAttachmentSubmissionWitness
{
    /// <summary>Every submission this was asked to settle, in order.</summary>
    public List<IReadOnlyList<SubmittedAttachmentBytes>> Settlements { get; } = [];

    /// <summary>What every settlement answers.</summary>
    public AttachmentSubmissionVerdict Answer { get; init; } = AttachmentSubmissionVerdict.Unavailable;

    public Task<AttachmentSubmissionVerdict> SettleAsync(
        IReadOnlyList<SubmittedAttachmentBytes> submitted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submitted);
        lock (Settlements)
        {
            Settlements.Add(submitted);
        }

        return Task.FromResult(Answer);
    }
}
