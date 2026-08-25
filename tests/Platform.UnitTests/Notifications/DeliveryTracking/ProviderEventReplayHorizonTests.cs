using NotificationHub.Api.Modules.Notifications.Features.Mutations;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

namespace NotificationHub.UnitTests.Notifications.DeliveryTracking;

/// <summary>
/// The freshness guarantee of a callback, which is two knobs that have to agree
/// and nothing else.
/// <para>
/// One provider signs a timestamp along with its payload, and the hub refuses a
/// callback outside a narrow window around it. The other signs the callback URL
/// and the form and no instant at all, so a captured callback of that channel
/// stays cryptographically valid for ever and the deduplication mark is the
/// only thing standing between it and a second acceptance. That makes the
/// retention of the mark a security parameter rather than a storage one.
/// </para>
/// <para>
/// It is not sufficient on its own either. Past the mark a replay is accepted
/// at the door again, and past the attempt window it finds no attempt to
/// describe, so the interval between the two is the only one in which a replay
/// could write evidence a second time. Keeping them equal closes it, and this
/// is the only place that says so.
/// </para>
/// </summary>
public sealed class ProviderEventReplayHorizonTests
{
    [Fact]
    public void The_dedupe_retention_covers_the_window_an_attempt_can_still_be_resolved_in()
        => new ProviderEventDedupePurgeOptions().Retention.ShouldBeGreaterThanOrEqualTo(
            NotificationPlanOutcome.AttemptWindow,
            "uma marca de dedupe apagada antes de a janela de resolução fechar deixa um intervalo "
            + "em que um callback capturado volta a ser aceito e ainda encontra o attempt que ele "
            + "descreve; é exatamente esse intervalo que a garantia declarada não cobre.");

    [Fact]
    public void The_attempt_window_covers_the_longest_notification_the_hub_accepts()
        => NotificationPlanOutcome.AttemptWindow.ShouldBeGreaterThanOrEqualTo(
            TimeSpan.FromSeconds(RequestNotification.MaxTtlSeconds),
            "a janela de resolução precisa alcançar o attempt mais velho que uma notificação viva "
            + "pode ter; abaixo do TTL máximo o hub descartaria em silêncio a confirmação de uma "
            + "entrega que ainda importa.");
}
