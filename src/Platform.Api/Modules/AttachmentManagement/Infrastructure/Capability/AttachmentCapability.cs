using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Capability;

/// <summary>
/// The one place the deployment state of the capability is asked about. It
/// exists so the question has a single name and a single reader: two call
/// sites reading the bound section directly would be two places for the
/// meaning of an absent section to drift apart.
/// <para>
/// It is asked at the door of this module and nowhere else. Whether
/// attachments are taken is a decision of the module that owns them, and a
/// gate placed in the vocabulary another module publishes to producers would
/// make the answer a property of one transport instead of a property of the
/// capability.
/// </para>
/// <para>
/// It is deliberately not consulted by anything that works on an attachment
/// that already exists. That is what makes switching it off a block on new
/// acceptances rather than a freeze of everything in flight.
/// </para>
/// </summary>
internal sealed class AttachmentCapability(IOptions<AttachmentCapabilityOptions> options)
{
    /// <summary>
    /// Whether a new attachment may be minted and a new set may be taken. The
    /// value is read on every ask rather than captured once, so an operator
    /// who reloads configuration is answered by what is in force now.
    /// </summary>
    internal bool AcceptsNewAttachments => options.Value.AcceptsNewAttachments;
}
