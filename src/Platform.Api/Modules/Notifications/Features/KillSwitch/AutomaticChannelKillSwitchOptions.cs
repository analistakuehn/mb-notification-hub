namespace NotificationHub.Api.Modules.Notifications.Features.KillSwitch;

/// <summary>
/// Whether a channel whose provider circuit stays open may stop itself, and
/// for how long the circuit must stay open before it does.
/// <para>
/// It ships disabled, and that is the decision rather than an oversight. The
/// circuit is observed per process and the kill switch is global, so a single
/// degraded instance, one bad node, one exhausted connection pool, can stop a
/// channel for the whole fleet while every other instance is sending fine.
/// The cost lands where it hurts most: SMS is the last step of the delivery
/// plan, so stopping that channel leaves authentication codes waiting until
/// they expire, and the person never learns why. Enabling it is an operational
/// decision taken with that trade in view, and the way back is always a human
/// reactivation through the administration surface: nothing here ever turns a
/// channel back on, because the condition that triggered the stop says nothing
/// about whether it is safe to resume.
/// </para>
/// </summary>
public sealed class AutomaticChannelKillSwitchOptions
{
    public const string SectionName = "Modules:Notifications:AutomaticChannelKillSwitch";

    /// <summary>Whether an open circuit may stop the channel; off by default.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// How long the circuit must stay continuously open before the channel
    /// stops. It is measured from the first refused send of a streak, and any
    /// call the pipeline lets through ends the streak, because a call that
    /// reached the provider is the proof that the circuit closed again.
    /// </summary>
    public TimeSpan OpenCircuitWindow { get; init; } = TimeSpan.FromMinutes(10);
}
