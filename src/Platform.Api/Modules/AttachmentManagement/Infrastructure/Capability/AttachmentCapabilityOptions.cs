namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Capability;

/// <summary>
/// Whether this deployment takes new attachments at all. It is the deployment
/// state of the capability and not an emergency stop, and the difference is
/// the whole reason it is a section of its own instead of a scope of the
/// emergency control that already exists.
/// <para>
/// The emergency control means permitted unless a row says otherwise, so a
/// missing row lets work through. This means the opposite: nothing is taken
/// unless configuration says so, because the capability is deployed switched
/// off and only turned on afterwards. Putting the two behind one artifact
/// would give the same absence opposite consequences depending on which of
/// them was being read, and an operator removing a row would turn a capability
/// on by accident.
/// </para>
/// <para>
/// The two also read differently in an incident. Blocked is an emergency and
/// somebody decided it just now; not enabled is where the deployment has not
/// arrived yet, and nobody decided anything today. An operator who confuses
/// them is looking for a decision that was never taken.
/// </para>
/// <para>
/// Unlike the capacity and retention sections beside it, nothing here is
/// refused at startup and nothing is required. Those sections refuse an unset
/// value because zero would be a product decision taken by an omission; here
/// the omission is the decision, and it is the safe one. In both cases absence
/// is the safe value, and safe is the opposite thing in each.
/// </para>
/// </summary>
public sealed class AttachmentCapabilityOptions
{
    public const string SectionName = "Modules:AttachmentManagement:Capability";

    /// <summary>
    /// Whether new attachments may enter: a registration that mints a
    /// reference, and a claim that takes a set for an acceptance that does not
    /// hold one yet.
    /// <para>
    /// It carries no initializer on purpose. The language default of the type
    /// is what answers a deployment whose configuration never names this
    /// section, and that answer has to be the closed one. An initializer here,
    /// of either value, would move the answer from the type to this line and
    /// let a later edit open the capability by touching one word.
    /// </para>
    /// <para>
    /// It says nothing about what is already accepted. Reading, attempting,
    /// reconciling, sweeping and investigating an attachment that exists do
    /// not ask this question, so switching it back off is a reversal that
    /// leaves every durable row where it is.
    /// </para>
    /// </summary>
    public bool AcceptsNewAttachments { get; init; }
}
