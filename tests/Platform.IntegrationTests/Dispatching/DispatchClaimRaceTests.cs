using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Dispatching;

[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class DispatchClaimRaceTests(CorePipelineFixture fixture)
{
    [RequiresDockerFact]
    public async Task Two_concurrent_claims_of_one_attempt_let_exactly_one_win_and_stamp_the_provider()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates");
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "transactional", ("email", null));
        (var recipientId, _, _) = await DispatchApi.RegisterRecipientAsync(fixture);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "transactional", recipientId, "core-transactional");

        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        using IServiceScope first = dispatcher.CreateScope();
        using IServiceScope second = dispatcher.CreateScope();
        NotificationAttempt attemptInFirst = await LoadAttemptAsync(first, notificationId);
        NotificationAttempt attemptInSecond = await LoadAttemptAsync(second, notificationId);

        // The induced race: two dispatchers claim the same queued attempt at
        // the same time; the optimistic lock lets exactly one through.
        Task<AttemptClaimOutcome> firstClaim = first.ServiceProvider
            .GetRequiredService<AttemptDispatchWriter>()
            .TryClaimAsync(attemptInFirst, "sendgrid", CancellationToken.None);
        Task<AttemptClaimOutcome> secondClaim = second.ServiceProvider
            .GetRequiredService<AttemptDispatchWriter>()
            .TryClaimAsync(attemptInSecond, "sendgrid", CancellationToken.None);
        AttemptClaimOutcome[] outcomes = await Task.WhenAll(firstClaim, secondClaim);

        outcomes.Count(outcome => outcome == AttemptClaimOutcome.Claimed).ShouldBe(1);
        outcomes.Count(outcome => outcome == AttemptClaimOutcome.NotQueued).ShouldBe(1);

        NotificationAttempt claimed = await fixture.QueryNotificationsDbAsync(
            db => db.NotificationAttempts
                .AsNoTracking()
                .SingleAsync(candidate => candidate.NotificationId == notificationId));
        claimed.Status.ShouldBe(NotificationAttemptStatuses.Sending);
        claimed.ProviderKey.ShouldBe("sendgrid");
    }

    private static async Task<NotificationAttempt> LoadAttemptAsync(
        IServiceScope scope,
        Guid notificationId)
        => await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>()
            .NotificationAttempts
            .SingleAsync(candidate => candidate.NotificationId == notificationId);
}
