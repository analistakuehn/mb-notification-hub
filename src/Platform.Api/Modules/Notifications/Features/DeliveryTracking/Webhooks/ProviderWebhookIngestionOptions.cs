using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Webhooks;

/// <summary>
/// Bounds of the only public surface of this hub. Both knobs exist because the
/// cost of a callback is linear in what it carries and the answer to it is
/// measured by the provider: a callback that takes too long is redelivered, and
/// a redelivery of an expensive callback is the failure mode amplifying itself
/// on the one route nobody outside can be asked to slow down.
/// <para>
/// Neither bound is a rate limit, which the route already has, and neither is a
/// security control. They are the ceiling that turns a per-event budget into a
/// per-callback one: without them the number of events in a batch is chosen by
/// the caller and the response time follows it.
/// </para>
/// </summary>
public sealed class ProviderWebhookIngestionOptions
{
    public const string SectionName = "Modules:Notifications:ProviderWebhookIngestion";

    /// <summary>
    /// Largest callback body this hub reads, in bytes. The default holds a
    /// batch far larger than either provider sends and is small enough that
    /// reading it, verifying its signature and sealing it stay inside the
    /// budget. Past it the request is refused before any of that work happens,
    /// because a body this size is a misconfiguration or an attack rather than
    /// delivery feedback, and paying to prove otherwise is the one thing this
    /// route must not do.
    /// </summary>
    [Range(4_096, 8_388_608)]
    public long MaxBodyBytes { get; init; } = 1_048_576;

    /// <summary>
    /// Most tracked events one callback may carry.
    /// <para>
    /// The cost it bounds is the response time of the only public surface of
    /// this hub, and that cost is linear: every event of a callback is a
    /// transaction of its own, because the deduplication claim, the evidence row
    /// and the outbox append have to commit together per event. Without a
    /// ceiling the number of transactions one request performs is chosen by the
    /// caller, and the provider redelivers whatever takes too long.
    /// </para>
    /// <para>
    /// The bytes ceiling does not stand in for this one. It bounds the body, and
    /// a body bounded at one mebibyte still holds thousands of events, which is
    /// thousands of transactions inside a single answer.
    /// </para>
    /// <para>
    /// The value is a judgement and is stated as one: what the ceiling has to
    /// keep is the answer inside the budget the provider measures, the per-event
    /// cost is measured by the delivery mode of the performance probe, and the
    /// right number belongs to the load gate running against real provider
    /// bodies rather than to this comment.
    /// </para>
    /// <para>
    /// The refusal is whole and it is not free: the provider redelivers, meets
    /// the same ceiling and eventually drops the batch, so the evidence of an
    /// over-sized callback is lost. That is the accepted side of the trade,
    /// because storing part of it would answer 202 over evidence this hub never
    /// took.
    /// </para>
    /// </summary>
    [Range(1, 10_000)]
    public int MaxEventsPerCallback { get; init; } = 200;
}
