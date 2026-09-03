using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Capacity;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Validation;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Dispatching;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.Dispatching;
using NotificationHub.IntegrationTests.TemplateManagement;
using Npgsql;

namespace NotificationHub.IntegrationTests.Notifications.AcceptedAttachments;

/// <summary>
/// What the dispatch does in the window between the claim of an attempt and
/// the call that cannot be taken back, when the set the notification was
/// accepted over is no longer one that may go out.
/// <para>
/// The snapshot on the row froze identity and composition and froze no
/// eligibility at all, so this is where eligibility is read: whether every
/// member is still released, still within its validity and still the content
/// it was accepted with, and whether the set still fits what a notification
/// may carry. A send that skipped this would deliver on a release taken back
/// an hour after the acceptance, which is the failure the whole capability
/// exists to prevent.
/// </para>
/// <para>
/// Every zero below sits next to a one. A provider that was never called is
/// what a path unable to call anything looks like too, so each refusal is
/// followed by a notification the very same worker, the very same provider and
/// the very same configuration do send.
/// </para>
/// </summary>
[Collection(AcceptedAttachmentFlowCollectionDefinition.Name)]
public sealed class AcceptedAttachmentPreflightTests(AcceptedAttachmentFlowFixture fixture)
{
    private const string SendGridAccepted = "sg-message-preflight";

    private static readonly (string Channel, string? Timeout)[] EmailOnly = [("email", null)];

    /// <summary>
    /// Long enough ago that a release granted then is past the validity that
    /// ships, and recent enough to be an ordinary date rather than an edge of
    /// the calendar.
    /// </summary>
    private static readonly TimeSpan BeyondTheValidity = TimeSpan.FromDays(40);

    /// <summary>
    /// The four ways a member stops being deliverable, walked over one
    /// arrangement, and then the notification that proves the walk was
    /// measuring refusals rather than a worker that could not send.
    /// <para>
    /// One test for the four because the owning module answers one word for
    /// them and the dispatch settles one code: what has to be shown is that
    /// each of them reaches that word, and four separate tests would repeat the
    /// arrangement without adding a claim.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task No_provider_is_called_when_a_member_of_the_accepted_set_stops_being_deliverable()
    {
        AttachmentArrangement arrangement = await AcceptedAttachmentFlow.ArrangeAsync(
            fixture, EmailOnly);

        // The expired one is accepted over a release granted long ago, and the
        // acceptance takes it: the claim reads the state and the release row
        // and never the age of either, so this is a set that was claimable
        // today and may not go out today.
        AttachedNotification expired = await AcceptedAttachmentFlow.AcceptAsync(
            fixture, arrangement, attachmentCount: 1,
            grantedAt: DateTimeOffset.UtcNow - BeyondTheValidity);
        AttachedNotification revoked = await AcceptedAttachmentFlow.AcceptAsync(
            fixture, arrangement, attachmentCount: 2);
        AttachedNotification superseded = await AcceptedAttachmentFlow.AcceptAsync(
            fixture, arrangement, attachmentCount: 1);
        AttachedNotification incomplete = await AcceptedAttachmentFlow.AcceptAsync(
            fixture, arrangement, attachmentCount: 2);
        AttachedNotification deliverable = await AcceptedAttachmentFlow.AcceptAsync(
            fixture, arrangement, attachmentCount: 2);
        await AcceptedAttachmentFlow.DispatchAllAsync(
            fixture,
            expired.NotificationId,
            revoked.NotificationId,
            superseded.NotificationId,
            incomplete.NotificationId,
            deliverable.NotificationId);

        await AcceptedAttachmentFlow.RevokeAsync(fixture, revoked.Attachments[1].Id);
        (await AcceptedAttachmentFlow.AttachmentStateAsync(fixture, revoked.Attachments[1].Id))
            .ShouldBe(AttachmentStates.Revoked);
        await AcceptedAttachmentFlow.SupersedeContentAsync(fixture, superseded.Attachments[0]);
        await AcceptedAttachmentFlow.ForgetReferenceAsync(fixture, incomplete.Attachments[0].Id);

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(
            202, null, new Dictionary<string, string> { ["X-Message-Id"] = SendGridAccepted }));
        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));

        foreach ((AttachedNotification refused, var cause) in new[]
        {
            (expired, "a liberação venceu antes do envio"),
            (revoked, "a liberação foi revogada depois do aceite"),
            (superseded, "o conteúdo aceito deixou de ser o conteúdo liberado"),
            (incomplete, "um membro do conjunto deixou de existir"),
        })
        {
            await AssertWithheldAsync(dispatcher, refused, cause);
            provider.RequestCount.ShouldBe(
                0,
                $"nenhuma chamada ao provedor pode acontecer quando {cause}.");
        }

        await SendAsync(dispatcher, deliverable);

        provider.RequestCount.ShouldBe(
            1,
            "a chamada única do vizinho entregável é o que prova que os quatro zeros acima "
            + "foram recusas, e não um worker incapaz de chamar o provedor.");
    }

    /// <summary>
    /// The expiry is a reading and never a stored deadline. The same release
    /// that was refused above goes out under a validity declared to have taken
    /// effect now, which is what keeps shortening the value from expiring, on
    /// the deployment itself, every release older than the new duration.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_release_past_its_stored_deadline_still_goes_out_under_the_grace_of_the_validity_in_force()
    {
        AttachmentArrangement arrangement = await AcceptedAttachmentFlow.ArrangeAsync(
            fixture, EmailOnly);
        AttachedNotification aged = await AcceptedAttachmentFlow.AcceptAsync(
            fixture, arrangement, attachmentCount: 1,
            grantedAt: DateTimeOffset.UtcNow - BeyondTheValidity);
        await AcceptedAttachmentFlow.DispatchAllAsync(fixture, aged.NotificationId);

        // The row itself says the release expired ten days ago, and nothing
        // below changes it: what changes is the instant the validity in force
        // is counted from.
        (await StoredExpiryAsync(aged.Attachments[0].Id))
            .ShouldBeLessThan(DateTimeOffset.UtcNow);

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(
            202, null, new Dictionary<string, string> { ["X-Message-Id"] = SendGridAccepted }));
        Dictionary<string, string?> settings = DispatchApi.ProviderSettings(
            provider.BaseAddress, provider.BaseAddress);
        settings[$"{AttachmentValidationOptions.SectionName}:ValidityEffectiveFrom"] =
            DateTimeOffset.UtcNow.ToString("O");
        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(settings);

        await SendAsync(dispatcher, aged);

        provider.RequestCount.ShouldBe(
            1,
            "contado a partir do instante em que a validade vigente passou a valer, o prazo "
            + "ainda não venceu, e a mesma liberação recusada sem carência sai com ela.");
    }

    /// <summary>
    /// The capacity is measured against the values in force at the send and
    /// never against the ones the acceptance ran under, so a ceiling tightened
    /// afterwards stops a set that was accepted under the older one.
    /// </summary>
    [RequiresDockerFact]
    public async Task No_provider_is_called_when_the_accepted_set_outgrows_the_envelope()
    {
        (AttachedNotification refused, AttachedNotification fitting, ServiceProvider dispatcher,
            FakeProviderServer provider) = await TightenedCapacityAsync(
            maxAttachmentBytes: "4096", maxEnvelopeBytes: "4096", maxAttachments: "10");

        await using (provider)
        await using (dispatcher)
        {
            await AssertOverCapacityAsync(dispatcher, refused);
            provider.RequestCount.ShouldBe(
                0,
                "um conjunto cuja soma ultrapassa o envelope não chega ao provedor.");

            await SendAsync(dispatcher, fitting);

            provider.RequestCount.ShouldBe(
                1,
                "o vizinho que cabe no mesmo envelope apertado prova que o zero acima foi "
                + "a medida do conjunto, e não uma composição incapaz de enviar.");
        }
    }

    /// <summary>
    /// The count is the other half of the capacity, and it is bounded on its
    /// own: the sum already bounds the bytes, and what this bounds is how many
    /// whole readings a send performs before the point of no return.
    /// </summary>
    [RequiresDockerFact]
    public async Task No_provider_is_called_when_the_accepted_set_outgrows_the_allowed_count()
    {
        (AttachedNotification refused, AttachedNotification fitting, ServiceProvider dispatcher,
            FakeProviderServer provider) = await TightenedCapacityAsync(
            maxAttachmentBytes: "7340032", maxEnvelopeBytes: "7340032", maxAttachments: "1");

        await using (provider)
        await using (dispatcher)
        {
            await AssertOverCapacityAsync(dispatcher, refused);
            provider.RequestCount.ShouldBe(
                0,
                "um conjunto com mais membros do que a quantidade máxima não chega ao provedor.");

            await SendAsync(dispatcher, fitting);

            provider.RequestCount.ShouldBe(
                1,
                "o vizinho de um único anexo prova que o zero acima foi a contagem do "
                + "conjunto, e não uma composição incapaz de enviar.");
        }
    }

    /// <summary>
    /// Nothing could be established about the set, so nothing is settled about
    /// the attempt: it goes back to the queue exactly as an open circuit
    /// returns it, with no verdict and no dedupe mark.
    /// <para>
    /// The repair reuses the very same trigger, and that is the second thing
    /// this proves. Had the refusal written a verdict or a mark, the attempt
    /// would be unsendable forever and a store that was briefly unreachable
    /// would have ended the notification.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task An_unreadable_record_holds_the_attempt_and_calls_no_provider()
    {
        AttachmentArrangement arrangement = await AcceptedAttachmentFlow.ArrangeAsync(
            fixture, EmailOnly);
        AttachedNotification accepted = await AcceptedAttachmentFlow.AcceptAsync(
            fixture, arrangement, attachmentCount: 2);
        await AcceptedAttachmentFlow.DispatchAllAsync(fixture, accepted.NotificationId);
        MessageEnvelope trigger = await TriggerAsync(accepted);

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(
            202, null, new Dictionary<string, string> { ["X-Message-Id"] = SendGridAccepted }));
        Dictionary<string, string?> unreachable = DispatchApi.ProviderSettings(
            provider.BaseAddress, provider.BaseAddress);
        unreachable[$"{AttachmentManagementEfOptions.SectionName}:ConnectionString"] =
            new NpgsqlConnectionStringBuilder(fixture.PostgresConnectionString)
            {
                Database = $"sem_anexos_{Guid.NewGuid():N}",
            }.ConnectionString;

        await using (ServiceProvider blinded = fixture.BuildDispatcherWorkerProvider(unreachable))
        {
            MessageDisposition held = await RunDispatchAsync(blinded, trigger);

            held.ShouldBeOfType<MessageDisposition.Postponed>()
                .Reason.ShouldBe(DispatchMessageProcessor.ReasonAttachmentsUnverified);
            provider.RequestCount.ShouldBe(
                0,
                "um conjunto sobre o qual nada foi estabelecido não chega ao provedor.");
            NotificationAttempt returned = await AttemptOf(accepted);
            returned.Status.ShouldBe(NotificationAttemptStatuses.Queued);
            returned.ProviderKey.ShouldBeNull();
            returned.ErrorCode.ShouldBeNull();
        }

        await using ServiceProvider healed = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));

        (await RunDispatchAsync(healed, trigger)).ShouldBeOfType<MessageDisposition.Processed>();

        provider.RequestCount.ShouldBe(
            1,
            "a mesma mensagem, depois que o registro voltou a ser legível, é o que prova que "
            + "nada foi liquidado nem marcado durante a recusa.");
        NotificationAttempt sent = await AttemptOf(accepted);
        sent.Status.ShouldBe(NotificationAttemptStatuses.Sent);
        sent.ProviderMessageId.ShouldBe(SendGridAccepted);
    }

    /// <summary>
    /// Accepts one set that the given capacity refuses and one that it still
    /// admits, under a single arrangement and a single worker composition.
    /// <para>
    /// The whole capacity is declared by the caller and never half of it, so
    /// each arm leaves exactly one rule able to refuse. An arm that tightened
    /// the count while leaving the envelope narrow would read as a count
    /// refusal and be a sum refusal, and removing the rule it claims to
    /// measure would not turn it red.
    /// </para>
    /// </summary>
    private async Task<(AttachedNotification Refused, AttachedNotification Fitting,
        ServiceProvider Dispatcher, FakeProviderServer Provider)> TightenedCapacityAsync(
        string maxAttachmentBytes,
        string maxEnvelopeBytes,
        string maxAttachments)
    {
        AttachmentArrangement arrangement = await AcceptedAttachmentFlow.ArrangeAsync(
            fixture, EmailOnly);
        AttachedNotification refused = await AcceptedAttachmentFlow.AcceptAsync(
            fixture, arrangement, attachmentCount: 2);
        AttachedNotification fitting = await AcceptedAttachmentFlow.AcceptAsync(
            fixture, arrangement, attachmentCount: 1);
        await AcceptedAttachmentFlow.DispatchAllAsync(
            fixture, refused.NotificationId, fitting.NotificationId);

        FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(
            202, null, new Dictionary<string, string> { ["X-Message-Id"] = SendGridAccepted }));
        Dictionary<string, string?> settings = DispatchApi.ProviderSettings(
            provider.BaseAddress, provider.BaseAddress);

        // The seeded members are 2048 and 2049 bytes, so an envelope of four
        // kibibytes sits between a set of two and a set of one, and so does a
        // count of one. The per attachment ceiling travels with the envelope
        // because the module refuses, at startup, a ceiling above it.
        settings[$"{AttachmentCapacityOptions.SectionName}:MaxAttachmentBytes"] = maxAttachmentBytes;
        settings[$"{AttachmentCapacityOptions.SectionName}:MaxEnvelopeBytes"] = maxEnvelopeBytes;
        settings[$"{AttachmentCapacityOptions.SectionName}:MaxAttachmentsPerNotification"] = maxAttachments;
        return (refused, fitting, fixture.BuildDispatcherWorkerProvider(settings), provider);
    }

    private async Task AssertWithheldAsync(
        ServiceProvider dispatcher,
        AttachedNotification refused,
        string cause)
    {
        MessageDisposition settled = await RunDispatchAsync(dispatcher, await TriggerAsync(refused));

        settled.ShouldBeOfType<MessageDisposition.Processed>();
        NotificationAttempt attempt = await AttemptOf(refused);
        attempt.Status.ShouldBe(NotificationAttemptStatuses.Failed);
        attempt.ErrorCode.ShouldBe(
            DispatchMessageProcessor.ErrorAttachmentsWithheld,
            $"o código estável da recusa é o mesmo quando {cause}.");
        attempt.ProviderMessageId.ShouldBeNull();
    }

    private async Task AssertOverCapacityAsync(
        ServiceProvider dispatcher,
        AttachedNotification refused)
    {
        MessageDisposition settled = await RunDispatchAsync(dispatcher, await TriggerAsync(refused));

        settled.ShouldBeOfType<MessageDisposition.Processed>();
        NotificationAttempt attempt = await AttemptOf(refused);
        attempt.Status.ShouldBe(NotificationAttemptStatuses.Failed);
        attempt.ErrorCode.ShouldBe(DispatchMessageProcessor.ErrorAttachmentsOverCapacity);
        attempt.ProviderMessageId.ShouldBeNull();
    }

    private async Task SendAsync(ServiceProvider dispatcher, AttachedNotification deliverable)
    {
        (await RunDispatchAsync(dispatcher, await TriggerAsync(deliverable)))
            .ShouldBeOfType<MessageDisposition.Processed>();
        NotificationAttempt sent = await AttemptOf(deliverable);
        sent.Status.ShouldBe(NotificationAttemptStatuses.Sent);
        sent.ProviderMessageId.ShouldBe(SendGridAccepted);
    }

    private async Task<MessageEnvelope> TriggerAsync(AttachedNotification accepted)
        => AcceptedAttachmentFlow.DispatchTrigger(
            accepted.NotificationId, (await AttemptOf(accepted)).Id);

    private async Task<NotificationAttempt> AttemptOf(AttachedNotification accepted)
        => (await AcceptedAttachmentFlow.AttemptsAsync(fixture, accepted.NotificationId))
            .ShouldHaveSingleItem();

    private async Task<DateTimeOffset> StoredExpiryAsync(Guid attachmentId)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>()
            .Releases
            .AsNoTracking()
            .Where(release => release.AttachmentId == attachmentId)
            .Select(release => release.ExpiresAt)
            .SingleAsync();
    }

    private static async Task<MessageDisposition> RunDispatchAsync(
        ServiceProvider dispatcher,
        MessageEnvelope envelope)
    {
        using IServiceScope scope = dispatcher.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<DispatchMessageProcessor>()
            .ProcessAsync(envelope, CancellationToken.None);
    }
}
