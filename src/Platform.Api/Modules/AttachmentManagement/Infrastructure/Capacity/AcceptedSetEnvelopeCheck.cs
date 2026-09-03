using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Capacity;

/// <summary>
/// Measures an accepted set against the configured capacity. The two numbers
/// are approved product limits and neither of them is this file's to choose,
/// which is why neither appears in it.
/// <para>
/// The measurement happens where the cost is about to be spent and never at
/// the acceptance, so a ceiling an operator tightens takes effect on the next
/// send rather than only on notifications accepted afterwards. That is the
/// case this exists for: a set that fitted when it was accepted and does not
/// fit now is refused before anything is spent on it.
/// </para>
/// </summary>
internal sealed class AcceptedSetEnvelopeCheck(IOptions<AttachmentCapacityOptions> options)
    : IAttachmentEnvelopeCheck
{
    public AttachmentEnvelopeVerdict Measure(AcceptedAttachmentSet accepted)
    {
        ArgumentNullException.ThrowIfNull(accepted);

        AttachmentCapacityOptions limits = options.Value;
        if (accepted.Count > limits.MaxAttachmentsPerNotification)
        {
            return AttachmentEnvelopeVerdict.Exceeded;
        }

        // Counted down from the ceiling rather than up to it. A sum of the
        // lengths could overflow before it was ever compared, and an overflowed
        // sum reads as a small number, which is the one wrong answer this
        // measurement must not be able to give. Nothing here can overflow: the
        // snapshot refuses a negative length, so the remainder only falls.
        var remaining = limits.MaxEnvelopeBytes;
        foreach (AcceptedAttachment item in accepted)
        {
            if (item.Length > remaining)
            {
                return AttachmentEnvelopeVerdict.Exceeded;
            }

            remaining -= item.Length;
        }

        return AttachmentEnvelopeVerdict.WithinEnvelope;
    }
}
