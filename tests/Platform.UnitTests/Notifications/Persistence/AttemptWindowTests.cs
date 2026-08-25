using NotificationHub.Api.Modules.Notifications.Features.Mutations;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

namespace NotificationHub.UnitTests.Notifications.Persistence;

/// <summary>
/// The window that bounds every query over the partition key of
/// <c>notification_attempt</c>: the step claim, the scheduler scans, the
/// retirement sweep and the correlation of a provider callback all carry it.
/// <para>
/// It is the one number in this module whose failure mode is silent in the
/// dangerous direction. Too wide only costs partitions the planner reads. Too
/// narrow drops rows the query was supposed to find, and one of those queries
/// decides whether a delivery confirmation is applied at all, which is the
/// event that ends a notification and calls off its fallback. Nothing about
/// that failure is visible from the outside, so the relationship that makes the
/// width safe is asserted here instead of kept in step by hand.
/// </para>
/// </summary>
public sealed class AttemptWindowTests
{
    [Fact]
    public void The_attempt_window_outlives_the_longest_notification_the_ingestion_accepts()
    {
        var longestNotification = TimeSpan.FromSeconds(RequestNotification.MaxTtlSeconds);

        NotificationPlanOutcome.AttemptWindow.ShouldBeGreaterThan(
            longestNotification,
            "a janela precisa conter todo attempt de uma notificação que viveu o TTL máximo; "
            + "menor que isso, uma consulta limitada por ela deixa de encontrar linhas que "
            + "existem, e a que correlaciona o callback do provedor descartaria em silêncio a "
            + "confirmação de entrega que encerra a notificação.");
    }

    /// <summary>
    /// Twice the bound, which is the width the module documents and depends on:
    /// an attempt can be queued at any point in the life of the notification,
    /// so the widest age an attempt reaches is the whole TTL, and the second
    /// span is what holds it with room left over for instants stamped by
    /// different processes.
    /// </summary>
    [Fact]
    public void The_window_is_twice_that_bound()
        => NotificationPlanOutcome.AttemptWindow.ShouldBeGreaterThanOrEqualTo(
            TimeSpan.FromSeconds(RequestNotification.MaxTtlSeconds) * 2);
}
