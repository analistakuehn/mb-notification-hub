using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Dispatch.Integration.V1;

/// <summary>
/// Answers, for one channel, whether a send through it composes the accepted
/// set into the provider call.
/// <para>
/// It exists so a caller that plans a send can ask before there is a send to
/// make. The answer comes from the adapter this deployment would actually
/// call, resolved the same way the send resolves it, so what the planner reads
/// and what the sender does are one object rather than two tables that can
/// drift. A published list of channels kept here would be that second table:
/// correct on the day it was written and silently wrong the day a channel is
/// pointed at another adapter.
/// </para>
/// <para>
/// Only a caller that carries a set has any business asking. The answer says
/// nothing about eligibility, about capacity or about whether the set may
/// still leave, and it is not a permission: it is one property of one adapter.
/// </para>
/// </summary>
public interface IChannelAttachmentSupport
{
    /// <summary>
    /// Whether the adapter configured for <paramref name="channel"/> carries
    /// an accepted set. A failure is the same deployment defect the provider
    /// resolution answers with, never a statement that the channel drops
    /// attachments: no configured row, or a row naming an adapter this process
    /// does not host.
    /// </summary>
    Task<Result<bool>> CarriesAttachmentsAsync(Channel channel, CancellationToken cancellationToken);
}
