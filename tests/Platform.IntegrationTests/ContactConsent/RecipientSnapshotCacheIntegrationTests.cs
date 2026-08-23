using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Reads;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.ContactConsent;

/// <summary>
/// The cache-aside directory of the worker side: a fresh entry answers from
/// Redis, the invalidation consumer marks it stale after a write, and the
/// last-known fallback serves critical flows when the local store degrades.
/// </summary>
[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class RecipientSnapshotCacheIntegrationTests(CorePipelineFixture fixture)
{
    [RequiresDockerFact]
    public async Task A_second_read_answers_from_the_cache_until_the_invalidation_consumer_marks_it_stale()
    {
        HttpClient contacts = fixture.CreateContactsClient("contacts-writer");
        var recipientId = ContactConsentApi.NewRecipientId();
        (await ContactConsentApi.PutContactPointsAsync(
            contacts,
            recipientId,
            ContactConsentApi.ContactPointsBody(
                [ContactConsentApi.ContactPoint("sms", "+5511988887777")])))
            .EnsureSuccessStatusCode();

        await using ServiceProvider worker = fixture.BuildContactConsentWorkerProvider();
        using IServiceScope scope = worker.CreateScope();
        IRecipientDirectory directory = scope.ServiceProvider.GetRequiredService<IRecipientDirectory>();

        Result<RecipientSnapshot> first = await directory.FindAsync(recipientId, CancellationToken.None);
        first.IsSuccess.ShouldBeTrue();
        first.Value!.ContactPoints.ShouldHaveSingleItem().Channel.ShouldBe("sms");

        // The store changes underneath: a second contact point joins. The
        // cached entry still answers, proving the read never hit the store.
        (await ContactConsentApi.PutContactPointsAsync(
            contacts,
            recipientId,
            ContactConsentApi.ContactPointsBody(
            [
                ContactConsentApi.ContactPoint("sms", "+5511988887777"),
                ContactConsentApi.ContactPoint("email", "cliente@example.com"),
            ])))
            .EnsureSuccessStatusCode();
        Result<RecipientSnapshot> cached = await directory.FindAsync(recipientId, CancellationToken.None);
        cached.Value!.ContactPoints.Count.ShouldBe(1);

        // The write emitted contacts-changed through the outbox; the relay
        // publishes it and the role's consumer marks the entry stale. The
        // queue may hold invalidations of other recipients too, so drain it.
        await using ServiceProvider relay = fixture.BuildRelayProvider();
        await CorePipelineFixture.RunRelayPassAsync(relay);
        var invalidations = 0;
        SqsConsumePassResult pass;
        while ((pass = await CorePipelineFixture.RunContactsChangedPassAsync(worker)).Received > 0)
        {
            invalidations += pass.Processed + pass.Duplicates;
        }

        invalidations.ShouldBeGreaterThanOrEqualTo(1);

        Result<RecipientSnapshot> revalidated = await directory.FindAsync(recipientId, CancellationToken.None);
        revalidated.Value!.ContactPoints.Count.ShouldBe(2);
    }

    [RequiresDockerFact]
    public async Task A_degraded_store_serves_the_last_known_snapshot_only_to_callers_that_declared_it()
    {
        HttpClient contacts = fixture.CreateContactsClient("contacts-writer");
        var recipientId = ContactConsentApi.NewRecipientId();
        (await ContactConsentApi.PutContactPointsAsync(
            contacts,
            recipientId,
            ContactConsentApi.ContactPointsBody(
                [ContactConsentApi.ContactPoint("sms", "+5511977776666")])))
            .EnsureSuccessStatusCode();

        // Warm the cache through a healthy provider, then mark it stale so
        // the next read must revalidate against the store.
        await using (ServiceProvider healthy = fixture.BuildContactConsentWorkerProvider())
        {
            using IServiceScope healthyScope = healthy.CreateScope();
            IRecipientDirectory directory =
                healthyScope.ServiceProvider.GetRequiredService<IRecipientDirectory>();
            (await directory.FindAsync(recipientId, CancellationToken.None)).IsSuccess.ShouldBeTrue();

            RecipientSnapshotCache cache = healthy.GetRequiredService<RecipientSnapshotCache>();
            await cache.MarkStaleAsync(recipientId, CancellationToken.None);
        }

        // A provider whose local store is unreachable: the stale entry is the
        // only source left.
        await using ServiceProvider degraded = fixture.BuildContactConsentWorkerProvider(
            new Dictionary<string, string?>
            {
                ["Modules:ContactConsent:Persistence:Ef:ConnectionString"] =
                    "Host=127.0.0.1;Port=1;Database=down;Username=none;Timeout=1;Command Timeout=1",
            });
        using IServiceScope degradedScope = degraded.CreateScope();
        IRecipientDirectory degradedDirectory =
            degradedScope.ServiceProvider.GetRequiredService<IRecipientDirectory>();

        Result<RecipientSnapshot> lastKnown = await degradedDirectory.FindAsync(
            recipientId, RecipientReadFallback.LastKnown, CancellationToken.None);
        lastKnown.IsSuccess.ShouldBeTrue();
        lastKnown.Value!.ContactPoints.ShouldHaveSingleItem().Channel.ShouldBe("sms");

        await Should.ThrowAsync<Exception>(async () => await degradedDirectory.FindAsync(
            recipientId, RecipientReadFallback.None, CancellationToken.None));
    }
}
