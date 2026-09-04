using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Retention;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Revocation;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Dispatching;
using NotificationHub.IntegrationTests.AttachmentManagement;
using NotificationHub.IntegrationTests.Compliance;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.Dispatching;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications.AcceptedAttachments;

/// <summary>
/// The reconstruction of a notification that carried attachments, read through
/// the authorized disclosure after a real send.
/// <para>
/// The whole capability rests on one asymmetry. The proof of which bytes went
/// out has to reach an auditor, or an accepted attachment is a file name and a
/// promise; the way to reach those bytes must not, or the answer stops being
/// evidence and becomes a second door to the content. This file measures both
/// halves on the same body, so the absence of the coordinates is asserted
/// beside the presence of the digests and neither can pass on an empty answer.
/// </para>
/// <para>
/// It lives beside the other suites of this flow rather than with the audit
/// ones because it plants a corrupted document on a notification row, and this
/// collection is the environment that exists for that.
/// </para>
/// </summary>
[Collection(AcceptedAttachmentFlowCollectionDefinition.Name)]
public sealed class AcceptedAttachmentEvidenceTests(AcceptedAttachmentFlowFixture fixture)
{
    private const string SendGridAccepted = "sg-message-evidence";

    private static readonly (string Channel, string? Timeout)[] EmailOnly = [("email", null)];

    /// <summary>
    /// Windows long enough that no attachment of a neighbouring suite can be
    /// abandoned under them. The sweep walks the table, so what keeps it off
    /// every other row of this environment is that only the arrangement below
    /// carries an instant older than ten years.
    /// </summary>
    private static readonly AttachmentRetentionWindows DecadeWindows = new(
        UnstartedUpload: TimeSpan.FromDays(3650),
        UnvalidatedContent: TimeSpan.FromDays(3650),
        RefusedContent: TimeSpan.FromDays(3650),
        WithdrawnRelease: TimeSpan.FromDays(3650));

    /// <summary>
    /// The whole claim of the task, on one body: a notification whose attempt a
    /// provider accepted, reconstructed from the row that is the authority on
    /// what was accepted.
    /// <para>
    /// The two attachments differ in name and in length on purpose, so each
    /// member of the answer is joined to a record of its own. A projection that
    /// resolved the set by position, or that answered every member with the
    /// same record, passes over one attachment and fails here.
    /// </para>
    /// <para>
    /// Every absence below stands next to a presence taken from the same rows
    /// by the same scan. The store, the key, the generation of the provider and
    /// the bytes themselves are read out of this module's own record for these
    /// exact attachments, so each of them is a value that really describes this
    /// notification and not a string nobody ever wrote.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task The_evidence_of_an_attempt_the_provider_accepted_carries_the_set_and_its_digests()
    {
        AttachedNotification accepted = await AcceptedAttachmentFlow.AcceptAsync(
            fixture, attachmentCount: 2, EmailOnly);
        await AcceptedAttachmentFlow.DispatchAllAsync(fixture, accepted.NotificationId);
        await SendAsync(accepted);

        StoredContent[] custody =
        [
            .. await Task.WhenAll(accepted.Attachments.Select(StoredContentAsync)),
        ];

        (var status, JsonElement body, var raw) = await ReadEvidenceAsync(accepted.NotificationId);
        status.ShouldBe(200);

        // The attempt the reconstruction is about: the provider took
        // responsibility for it, which is the precondition of the whole claim.
        JsonElement attempt = AuditApi.Items(body.GetProperty("state"), "attempts")
            .ShouldHaveSingleItem();
        attempt.GetProperty("status").GetString().ShouldBe("sent");
        attempt.GetProperty("providerMessageId").GetString().ShouldBe(SendGridAccepted);

        IReadOnlyList<JsonElement> members = AuditApi.Items(
            body.GetProperty("state").GetProperty("attachments"), "accepted");
        members.Count.ShouldBe(2);
        members.Select(member => member.GetProperty("reference").GetString())
            .ShouldBe(accepted.Attachments.Select(attachment => attachment.Reference));

        for (var index = 0; index < members.Count; index++)
        {
            SeededAttachment seeded = accepted.Attachments[index];
            StoredContent stored = custody[index];
            JsonElement member = members[index];

            // What the acceptance froze, from the notification row.
            member.GetProperty("contentIdentity").GetString().ShouldBe(seeded.ContentIdentity);
            member.GetProperty("name").GetString().ShouldBe(seeded.Name);
            member.GetProperty("mediaType").GetString().ShouldBe(seeded.MediaType);
            member.GetProperty("length").GetInt64().ShouldBe(seeded.Length);

            // What the owning module records about those exact bytes.
            JsonElement recorded = member.GetProperty("recorded");
            recorded.GetProperty("reference").GetString().ShouldBe(seeded.Reference);
            recorded.GetProperty("application").GetString().ShouldBe(accepted.Application);
            recorded.GetProperty("state").GetString().ShouldBe(AttachmentStates.Released);
            recorded.GetProperty("digestAlgorithm").GetString().ShouldBe("sha-256");
            recorded.GetProperty("digest").GetString().ShouldBe(stored.Digest);
            recorded.GetProperty("digestedLengthBytes").GetInt64().ShouldBe(seeded.Length);
            recorded.GetProperty("releasedAt").GetDateTimeOffset().ShouldBeGreaterThan(default);
            recorded.TryGetProperty("revokedAt", out _).ShouldBeFalse();
        }

        // The digest of one member is not the digest of the other, so the
        // comparison above is about the join and not about a value both
        // members happen to share.
        custody[0].Digest.ShouldNotBe(custody[1].Digest);

        AssertReconstructsWithoutTheWayToTheContent(raw, accepted, custody);
    }

    /// <summary>
    /// A notification that named no attachments and one whose stored document
    /// nobody can read, asked of the same route in the same test. The pair is
    /// the measurement: apart, each answer is a shape, and only together do
    /// they show that the surface tells a fact from an ignorance.
    /// <para>
    /// Telling an auditor that a notification carried no attachments, when what
    /// happened is that its snapshot is corrupt, is the worst answer this
    /// surface can give. It is not a partial answer, it is a wrong one, and the
    /// reader who acts on it stops looking.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task A_snapshot_nobody_can_read_never_answers_as_a_notification_without_attachments()
    {
        AttachmentArrangement arrangement = await AcceptedAttachmentFlow.ArrangeAsync(
            fixture, EmailOnly);
        AttachedNotification bare = await AcceptedAttachmentFlow.AcceptWithoutAttachmentsAsync(
            fixture, arrangement);
        AttachedNotification corrupted = await AcceptedAttachmentFlow.AcceptAsync(
            fixture, arrangement, attachmentCount: 1);
        await AcceptedAttachmentFlow.PlantAsync(
            fixture,
            corrupted.NotificationId,
            AcceptedAttachmentFlow.UnknownVersionDocument(corrupted));

        JsonElement none = (await ReadEvidenceAsync(bare.NotificationId)).Body
            .GetProperty("state").GetProperty("attachments");
        JsonElement unreadable = (await ReadEvidenceAsync(corrupted.NotificationId)).Body
            .GetProperty("state").GetProperty("attachments");

        // A list that is there and empty: the notification named none, and the
        // answer says so.
        none.GetProperty("accepted").GetArrayLength().ShouldBe(0);
        none.TryGetProperty("unreadable", out _).ShouldBeFalse();

        // No list at all, plus the shape of the defect from the closed
        // vocabulary of the module that owns the document.
        unreadable.TryGetProperty("accepted", out _).ShouldBeFalse(
            "um documento ilegível não pode chegar ao auditor como conjunto nenhum de anexos.");
        unreadable.GetProperty("unreadable").GetString()
            .ShouldBe(AcceptedAttachmentManifest.RefusedUnknownSchemaVersion);

        // The refusal names the shape and never quotes the document: the
        // reference, the handle and the file name of the corrupted set stay out
        // of the answer that reports the corruption.
        var raw = JsonSerializer.Serialize(unreadable);
        foreach (var sentinel in corrupted.Sentinels)
        {
            raw.Contains(sentinel, StringComparison.Ordinal).ShouldBeFalse(
                "a recusa nomeia a forma do defeito e nunca o conteúdo do documento.");
        }

        // The row is repaired before the suite moves on. The message the
        // acceptance published is still on the queue, and leaving a corrupted
        // row behind would hand a neighbour a refusal it never arranged.
        await AcceptedAttachmentFlow.PlantAsync(
            fixture, corrupted.NotificationId, AcceptedAttachmentFlow.WholeDocument(corrupted));
        (await AcceptedAttachmentFlow.StoredSetAsync(fixture, corrupted.NotificationId))
            .Count.ShouldBe(1);
    }

    /// <summary>
    /// The record outlives the bytes, and this is what that is worth: an
    /// attempt a provider accepted stays reconstructible after the sweep has
    /// taken the content it carried.
    /// <para>
    /// The sweep is the real one and it removes the real bytes, confirmed
    /// against the store on both sides of the round. What survives is the
    /// generation row, so the digest, the algorithm, the length and the instant
    /// of the capture still answer, and the state is what says the content is
    /// gone.
    /// </para>
    /// <para>
    /// The whole arrangement is dated twenty years back so that no other row of
    /// this environment can be due under the ten-year windows the round runs
    /// with. The count the round reports is the tripwire: a second discard
    /// would mean the sweep reached a neighbour and the measurement is not
    /// about this notification.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task The_attempt_stays_reconstructible_after_the_sweep_took_the_bytes()
    {
        AttachedNotification accepted = await AcceptedAttachmentFlow.AcceptAsync(
            fixture, attachmentCount: 1, EmailOnly);
        await AcceptedAttachmentFlow.DispatchAllAsync(fixture, accepted.NotificationId);
        await SendAsync(accepted);

        SeededAttachment seeded = accepted.Attachments.ShouldHaveSingleItem();
        StoredContent stored = await StoredContentAsync(seeded);
        (await OpenStatusAsync(stored)).ShouldBe(
            AttachmentStoreOpenStatus.Opened,
            "os bytes precisam estar na custódia antes da rodada, ou o zero depois dela não "
            + "mede remoção nenhuma.");

        var reason = $"revogado-{Guid.NewGuid():N}"[..40];
        await RevokeLongAgoAsync(seeded, reason);
        await EndEveryHoldAsync(seeded);

        AttachmentAbandonmentResult round = await RunSweepAsync();
        round.Discarded.ShouldBe(
            1,
            "só o anexo desta notificação está abandonado sob janelas de dez anos; um segundo "
            + "descarte significa que a rodada alcançou linha de vizinho.");

        (await AttachmentStateAsync(seeded)).ShouldBe(AttachmentStates.Discarded);
        (await OpenStatusAsync(stored)).ShouldBe(AttachmentStoreOpenStatus.Missing);

        (var status, JsonElement body, var raw) = await ReadEvidenceAsync(accepted.NotificationId);
        status.ShouldBe(200);

        JsonElement attempt = AuditApi.Items(body.GetProperty("state"), "attempts")
            .ShouldHaveSingleItem();
        attempt.GetProperty("status").GetString().ShouldBe("sent");
        attempt.GetProperty("providerMessageId").GetString().ShouldBe(SendGridAccepted);

        JsonElement member = AuditApi
            .Items(body.GetProperty("state").GetProperty("attachments"), "accepted")
            .ShouldHaveSingleItem();
        member.GetProperty("name").GetString().ShouldBe(seeded.Name);
        member.GetProperty("length").GetInt64().ShouldBe(seeded.Length);

        JsonElement recorded = member.GetProperty("recorded");
        recorded.GetProperty("digest").GetString().ShouldBe(
            stored.Digest,
            "o resumo criptográfico é o que ainda diz quais bytes saíram depois de eles serem "
            + "recolhidos, e ele sobrevive porque a linha da geração sobrevive.");
        recorded.GetProperty("digestedLengthBytes").GetInt64().ShouldBe(seeded.Length);
        recorded.GetProperty("state").GetString().ShouldBe(AttachmentStates.Discarded);
        recorded.GetProperty("revokedAt").GetDateTimeOffset().ShouldBeLessThan(DateTimeOffset.UtcNow);
        recorded.GetProperty("revocationReason").GetString().ShouldBe(reason);

        AssertReconstructsWithoutTheWayToTheContent(raw, accepted, [stored]);
    }

    /// <summary>
    /// The two halves of the rule, asserted on the same body with the same
    /// scan: what an auditor needs is there, and what would let anybody fetch
    /// the content is not.
    /// <para>
    /// Every value scanned for is read out of the durable record of these exact
    /// attachments, so the absences are absences of real coordinates of this
    /// very notification. A scan for strings nobody ever wrote would pass over
    /// any answer at all, and the presences are what keep this one honest: they
    /// come from the same rows, through the same comparison, over the same
    /// text.
    /// </para>
    /// </summary>
    private static void AssertReconstructsWithoutTheWayToTheContent(
        string raw,
        AttachedNotification accepted,
        IReadOnlyList<StoredContent> custody)
    {
        raw.ShouldNotBeEmpty();

        foreach (var expected in accepted.Sentinels.Concat(custody.Select(item => item.Digest)))
        {
            raw.Contains(expected, StringComparison.Ordinal).ShouldBeTrue(
                "a reconstrução precisa nomear a referência, o manipulador, o nome do arquivo "
                + "e o resumo criptográfico de cada membro do conjunto aceito.");
        }

        foreach (StoredContent stored in custody)
        {
            foreach ((var probe, var kind) in stored.WaysToTheContent())
            {
                raw.Contains(probe, StringComparison.Ordinal).ShouldBeFalse(
                    $"a evidência não carrega {kind}: isso é capacidade de alcançar os bytes, "
                    + "e não prova de quais bytes eram.");
            }
        }
    }

    private async Task<(int Status, JsonElement Body, string Raw)> ReadEvidenceAsync(
        Guid notificationId)
    {
        using HttpClient auditor = fixture.CreateAuditorClient(AuditApi.AuditorSubject);
        return await AuditApi.ReadAsync(auditor, AuditApi.EvidencePath(notificationId));
    }

    /// <summary>
    /// One send against a provider that accepts, over the very attempt the
    /// pipeline queued. The identity the provider hands back is what the
    /// evidence later reports as acceptance.
    /// </summary>
    private async Task SendAsync(AttachedNotification accepted)
    {
        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(
            202, null, new Dictionary<string, string> { ["X-Message-Id"] = SendGridAccepted }));
        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));

        NotificationAttempt queued = (await AcceptedAttachmentFlow.AttemptsAsync(
            fixture, accepted.NotificationId)).ShouldHaveSingleItem();
        using (IServiceScope scope = dispatcher.CreateScope())
        {
            (await scope.ServiceProvider
                .GetRequiredService<DispatchMessageProcessor>()
                .ProcessAsync(
                    AcceptedAttachmentFlow.DispatchTrigger(accepted.NotificationId, queued.Id),
                    CancellationToken.None))
                .ShouldBeOfType<MessageDisposition.Processed>();
        }

        NotificationAttempt sent = (await AcceptedAttachmentFlow.AttemptsAsync(
            fixture, accepted.NotificationId)).ShouldHaveSingleItem();
        sent.Status.ShouldBe(NotificationAttemptStatuses.Sent);
        sent.ProviderMessageId.ShouldBe(SendGridAccepted);
    }

    /// <summary>
    /// The durable record behind one accepted handle, with the bytes read back
    /// out of custody. The bytes are taken here, while they are still there, so
    /// a case that removes them afterwards still has them to scan the answer
    /// for.
    /// </summary>
    private async Task<StoredContent> StoredContentAsync(SeededAttachment seeded)
    {
        Guid generationId = AttachmentContentIdentity.GenerationOf(seeded.ContentIdentity)
            .ShouldNotBeNull();
        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentManagementDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>();
        AttachmentObjectGeneration generation = await dbContext.ObjectGenerations
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == generationId);
        Guid contentId = await dbContext.Attachments
            .AsNoTracking()
            .Where(candidate => candidate.Id == seeded.Id)
            .Select(candidate => candidate.ContentId)
            .SingleAsync();

        using AttachmentStoreOpen open = await scope.ServiceProvider
            .GetRequiredService<IAttachmentObjectStore>()
            .OpenAsync(generation.Locator(), CancellationToken.None);
        open.Status.ShouldBe(AttachmentStoreOpenStatus.Opened);
        using var buffer = new MemoryStream();
        await open.Content!.CopyToAsync(buffer);

        return new StoredContent(
            generation.Locator(),
            Convert.ToHexStringLower(generation.Digest),
            contentId,
            buffer.ToArray());
    }

    private async Task<AttachmentStoreOpenStatus> OpenStatusAsync(StoredContent stored)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        using AttachmentStoreOpen open = await scope.ServiceProvider
            .GetRequiredService<IAttachmentObjectStore>()
            .OpenAsync(stored.Locator, CancellationToken.None);
        return open.Status;
    }

    private Task<string> AttachmentStateAsync(SeededAttachment seeded)
        => AcceptedAttachmentFlow.AttachmentStateAsync(fixture, seeded.Id);

    /// <summary>
    /// Takes the release back through the module's own operation, dated far
    /// enough in the past that the sweep below can be run with windows no other
    /// row of this environment reaches.
    /// </summary>
    private async Task RevokeLongAgoAsync(SeededAttachment seeded, string reason)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentRevocationOperation revocation = new(
            scope.ServiceProvider.GetRequiredService<AttachmentManagementDbContext>(),
            scope.ServiceProvider.GetRequiredService<IAttachmentSaveOperation>(),
            new MutableTimeProvider(DateTimeOffset.UtcNow.AddYears(-20)),
            scope.ServiceProvider.GetRequiredService<ILogger<AttachmentRevocationOperation>>());
        (await revocation.RevokeAsync(
                AttachmentReference.Create(seeded.Reference).Value.ShouldNotBeNull(),
                reason,
                CancellationToken.None))
            .ShouldBe(AttachmentRevocationStatus.Revoked);
    }

    /// <summary>
    /// Ends every hold the send left over the attachment. The disposal refuses
    /// while one is live, and that refusal is a rule of its own rather than the
    /// subject here.
    /// </summary>
    private async Task EndEveryHoldAsync(SeededAttachment seeded)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentManagementDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>();
        AttachmentReference reference = AttachmentReference.Create(seeded.Reference)
            .Value.ShouldNotBeNull();
        List<string> holders = await dbContext.AttachmentDependencies
            .AsNoTracking()
            .Where(dependency => dependency.AttachmentId == seeded.Id
                && dependency.ReleasedAt == null)
            .Select(dependency => dependency.Holder)
            .ToListAsync();

        AttachmentDependencyRegistry registry = scope.ServiceProvider
            .GetRequiredService<AttachmentDependencyRegistry>();
        foreach (var holder in holders)
        {
            (await registry.ReleaseAsync(reference, holder, CancellationToken.None))
                .ShouldBe(AttachmentDependencyOutcome.Recorded);
        }

        (await dbContext.AttachmentDependencies
            .AsNoTracking()
            .CountAsync(dependency => dependency.AttachmentId == seeded.Id
                && dependency.ReleasedAt == null))
            .ShouldBe(0);
    }

    /// <summary>One round of the real sweep, composed as the maintenance role composes it.</summary>
    private async Task<AttachmentAbandonmentResult> RunSweepAsync()
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        AttachmentManagementDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>();
        IAttachmentObjectStore store = scope.ServiceProvider
            .GetRequiredService<IAttachmentObjectStore>();
        AttachmentAbandonmentScan scan = new(
            dbContext,
            new AttachmentDisposal(
                dbContext,
                store,
                scope.ServiceProvider.GetRequiredService<ILogger<AttachmentDisposal>>()),
            scope.ServiceProvider.GetRequiredService<IAttachmentObjectInventory>(),
            store,
            Options.Create(new AttachmentRetentionOptions
            {
                UnstartedUpload = DecadeWindows.UnstartedUpload,
                UnvalidatedContent = DecadeWindows.UnvalidatedContent,
                RefusedContent = DecadeWindows.RefusedContent,
                WithdrawnRelease = DecadeWindows.WithdrawnRelease,
            }),
            TimeProvider.System,
            scope.ServiceProvider.GetRequiredService<ILogger<AttachmentAbandonmentScan>>());
        return await scan.RunAsync(CancellationToken.None);
    }

    /// <summary>
    /// What this module durably holds behind one accepted handle: the proof of
    /// the bytes, which belongs in the answer, and everything that would let
    /// somebody reach them, which does not.
    /// </summary>
    private sealed record StoredContent(
        AttachmentObjectLocator Locator,
        string Digest,
        Guid ContentId,
        byte[] Bytes)
    {
        /// <summary>
        /// Every value that is capacity to reach these bytes rather than proof
        /// of them, each with the name of what finding it would mean. The
        /// content itself is scanned for in the two forms a leak could
        /// plausibly take, and in a prefix of each, so a copy cut short is not
        /// missed by a comparison that only knows the whole.
        /// </summary>
        internal IEnumerable<(string Probe, string Kind)> WaysToTheContent()
        {
            yield return (Locator.Store, "o nome da loja de objetos");
            yield return (Locator.Key, "a chave do objeto");
            yield return (Locator.Version, "a geração do provedor");
            yield return (ContentId.ToString("N"), "o identificador que deriva a chave");
            yield return (ContentId.ToString("D"), "o identificador que deriva a chave");
            yield return (Convert.ToHexStringLower(Bytes), "os bytes do conteúdo");
            yield return (Convert.ToBase64String(Bytes), "os bytes do conteúdo");
            yield return (Convert.ToHexStringLower(Bytes.AsSpan(0, 32)), "um prefixo do conteúdo");
            yield return (Convert.ToBase64String(Bytes.AsSpan(0, 33)), "um prefixo do conteúdo");
        }
    }
}
