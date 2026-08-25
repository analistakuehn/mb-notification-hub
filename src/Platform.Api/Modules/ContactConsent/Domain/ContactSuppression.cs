namespace NotificationHub.Api.Modules.ContactConsent.Domain;

/// <summary>Where a suppression came from; recorded on the row and in the trail.</summary>
public static class SuppressionSources
{
    /// <summary>A provider refused the destination and the delivery feedback accumulated to the rule.</summary>
    public const string ProviderFeedback = "provider-feedback";

    /// <summary>An operator suppressed the contact point by hand.</summary>
    public const string Manual = "manual";
}

/// <summary>
/// How many refusals a channel needs, and inside which window, before the hub
/// stops addressing the contact point.
/// </summary>
/// <param name="Occurrences">Refusals required.</param>
/// <param name="Window">
/// Span the refusals must share, counted back from the newest one and
/// inclusive at its edge. Null means the refusals never expire, which only
/// makes sense together with a single required occurrence.
/// </param>
public sealed record SuppressionThreshold(int Occurrences, TimeSpan? Window);

/// <summary>
/// The accumulation rule, per channel, and the only place that decides whether
/// a refusal costs the recipient a channel.
/// <para>
/// E-mail suppresses on the first definitive refusal: a mailbox the provider
/// declares nonexistent does not come back, and continuing to write to it
/// costs sender reputation for every message.
/// </para>
/// <para>
/// Every other channel requires two refusals inside a week. A number can be
/// refused for reasons that pass, a carrier condition or a device out of
/// service, and removing the channel on one such refusal would take away a
/// reachable destination, which on an authentication flow is the difference
/// between an inconvenience and a locked-out customer.
/// </para>
/// </summary>
public static class SuppressionRules
{
    private static readonly SuppressionThreshold OnFirstRefusal = new(Occurrences: 1, Window: null);

    private static readonly SuppressionThreshold TwiceInAWeek =
        new(Occurrences: 2, Window: TimeSpan.FromDays(7));

    /// <summary>The threshold in force for one channel.</summary>
    public static SuppressionThreshold For(string channel)
        => channel == ContactChannels.Email ? OnFirstRefusal : TwiceInAWeek;

    /// <summary>
    /// Whether the recorded refusals meet the channel's threshold. The window
    /// is measured back from <paramref name="latestObservation"/>, so an
    /// isolated refusal ages out instead of waiting forever for a partner.
    /// </summary>
    public static bool IsMet(
        string channel,
        IReadOnlyCollection<DateTimeOffset> observations,
        DateTimeOffset latestObservation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(observations);

        SuppressionThreshold threshold = For(channel);
        if (threshold.Window is not { } window)
        {
            return observations.Count >= threshold.Occurrences;
        }

        DateTimeOffset floor = latestObservation - window;
        return observations.Count(observation => observation >= floor) >= threshold.Occurrences;
    }
}

/// <summary>
/// One refusal of a destination, exactly as the delivery-feedback path
/// reported it. The row is evidence and never changes: the accumulation rule
/// reads the set of them, and a repeated report of the same source event
/// collides on the unique key instead of inflating the count.
/// </summary>
public sealed class SuppressionSignalRecord
{
    private SuppressionSignalRecord()
    {
        Channel = null!;
        Reason = null!;
    }

    public Guid Id { get; private set; }

    public Guid ContactPointId { get; private set; }

    public string Channel { get; private set; }

    /// <summary>Stable classification of the refusal, as the provider side named it.</summary>
    public string Reason { get; private set; }

    /// <summary>Evidence row that originated the report; unique, and the whole idempotency of this path.</summary>
    public Guid SourceEventId { get; private set; }

    /// <summary>When the reporting hub observed the refusal.</summary>
    public DateTimeOffset ObservedAt { get; private set; }

    public static SuppressionSignalRecord Report(
        Guid contactPointId,
        string channel,
        string reason,
        Guid sourceEventId,
        DateTimeOffset observedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (!ContactChannels.IsCanonical(channel))
        {
            throw new ArgumentException($"Canal de contato desconhecido: '{channel}'.", nameof(channel));
        }

        return new SuppressionSignalRecord
        {
            Id = Guid.CreateVersion7(),
            ContactPointId = contactPointId,
            Channel = channel,
            Reason = reason,
            SourceEventId = sourceEventId,
            ObservedAt = observedAt,
        };
    }
}

/// <summary>
/// One contact point the hub stopped addressing. A suppression is always
/// reversible and always attributable: the row records who created it and,
/// after a removal, who took it back, and the removal stamps the row instead
/// of deleting it, because the reason a message was never sent has to survive
/// the reversal.
/// </summary>
public sealed class ContactSuppression
{
    private ContactSuppression()
    {
        Channel = null!;
        Reason = null!;
        Source = null!;
        ActorType = null!;
        ActorId = null!;
    }

    public Guid Id { get; private set; }

    public Guid ContactPointId { get; private set; }

    public string Channel { get; private set; }

    public string Reason { get; private set; }

    /// <summary>Provider feedback or an operator; see <see cref="SuppressionSources"/>.</summary>
    public string Source { get; private set; }

    public string ActorType { get; private set; }

    public string ActorId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>End of a bounded suppression; null while it holds until a removal.</summary>
    public DateTimeOffset? Until { get; private set; }

    /// <summary>Stamped by the reversal; null while the suppression is in force.</summary>
    public DateTimeOffset? RemovedAt { get; private set; }

    public string? RemovedBy { get; private set; }

    public bool IsInForce => RemovedAt is null;

    public static ContactSuppression Create(
        Guid contactPointId,
        string channel,
        string reason,
        string source,
        string actorType,
        string actorId,
        DateTimeOffset now,
        DateTimeOffset? until = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorType);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (!ContactChannels.IsCanonical(channel))
        {
            throw new ArgumentException($"Canal de contato desconhecido: '{channel}'.", nameof(channel));
        }

        return new ContactSuppression
        {
            Id = Guid.CreateVersion7(),
            ContactPointId = contactPointId,
            Channel = channel,
            Reason = reason,
            Source = source,
            ActorType = actorType,
            ActorId = actorId,
            CreatedAt = now,
            Until = until,
        };
    }

    /// <summary>
    /// Takes the suppression back, naming who did it. Idempotent: a second
    /// removal keeps the first instant and the first actor, so a repeated
    /// request never rewrites who reversed the decision.
    /// </summary>
    public bool Remove(DateTimeOffset now, string removedBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(removedBy);
        if (RemovedAt is not null)
        {
            return false;
        }

        RemovedAt = now;
        RemovedBy = removedBy;
        return true;
    }
}
