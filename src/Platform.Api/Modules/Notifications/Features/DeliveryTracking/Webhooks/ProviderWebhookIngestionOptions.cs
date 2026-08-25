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
    /// The cost it bounds is not linear, and that is why the value is what it
    /// is. Every event of a callback is stored with the sealed bytes of the
    /// whole callback as its evidence, because the evidence of one event in a
    /// batch is the batch. So a callback of N events whose body grows with N
    /// writes N copies of a body of size N: the write volume is quadratic in
    /// the batch size, not linear in it. At roughly 420 bytes per event that is
    /// 1 MiB at fifty events, 16 MiB at two hundred and 100 MiB at five
    /// hundred, for a single request on the one route this hub exposes.
    /// </para>
    /// <para>
    /// Two hundred is the bound that keeps the worst case at tens of megabytes
    /// rather than hundreds. It is a judgement and it is stated as one: the
    /// arithmetic above is certain, the right ceiling is not, and it belongs to
    /// the load gate measuring real provider bodies. What would remove the
    /// question instead of bounding it is storing the body once and referencing
    /// it from the event rows, which is a change to the evidence model and not
    /// to this knob.
    /// </para>
    /// <para>
    /// The refusal is deliberate and it is not free: the provider redelivers,
    /// meets the same ceiling and eventually drops the batch, so the evidence
    /// of an over-sized callback is lost. That is the accepted side of the
    /// trade, because the alternative is a request that writes hundreds of
    /// megabytes on the route whose slowness is what triggers the redelivery in
    /// the first place.
    /// </para>
    /// </summary>
    [Range(1, 10_000)]
    public int MaxEventsPerCallback { get; init; } = 200;
}
