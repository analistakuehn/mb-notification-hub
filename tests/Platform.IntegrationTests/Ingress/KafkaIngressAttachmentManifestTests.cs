using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications.Features.Ingress;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.IntegrationTests.AttachmentManagement;
using NotificationHub.IntegrationTests.TemplateManagement;
using StackExchange.Redis;

namespace NotificationHub.IntegrationTests.Ingress;

/// <summary>
/// The manifest of attachment references over the bus. A producer that
/// publishes one is asking for the files it names, and the transport either
/// carries the member or refuses the body that names it. Binding the body and
/// dropping the member is the answer neither of those two is: the producer
/// receives the acceptance of a notification without the attachments it asked
/// for, with valid syntax and a different effect.
/// </summary>
[Collection(KafkaIngressCollectionDefinition.Name)]
public sealed class KafkaIngressAttachmentManifestTests(KafkaIngressFixture fixture)
{
    private const string Producer = KafkaIngressFixture.RequestedProducer;
    private const string AcceptedEventType = "notification.accepted";

    private const int ReadAttempts = 5;

    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The window this closes. While the member was dropped, a body that asked
    /// for files and a body that asked for none were the same request: under
    /// one idempotency key the second was answered with the acceptance of the
    /// first, so the producer was told a delivery carrying its files had
    /// happened and none ever existed.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_published_manifest_reaches_the_identity_of_the_request_that_carried_it()
    {
        var application = KafkaIngressApi.NewApplication();
        (var templateKey, _) =
            await KafkaIngressApi.CreatePublishedTemplateAsync(fixture, application, "transactional");
        await fixture.SeedProducerGrantsAsync((Producer, application, "transactional"));
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();

        // Attachments this application holds and had released: a manifest is
        // claimed on the way in, so text that names no attachment is refused
        // before it can become a notification.
        SeededAttachment first = await ClaimableAttachments.ReleasedAsync(fixture, application);
        SeededAttachment second = await ClaimableAttachments.ReleasedAsync(fixture, application);
        var withManifest = WithManifest(
            Event(application, templateKey, recipientId, idempotencyKey),
            Manifest(first.Reference, second.Reference));
        var withoutManifest = Event(application, templateKey, recipientId, idempotencyKey);

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        KafkaDisposition accepted = await ProcessAsync(provider, recipientId, withManifest);

        // The falsifying half, first: the same request published twice is one
        // request, so the comparison below answers over the manifest and not
        // over some difference every second record carries.
        KafkaDisposition republished = await ProcessAsync(provider, recipientId, withManifest);

        // Both authorities answer the same question: the cached acceptance
        // first, then the registration the acceptance persisted.
        KafkaDisposition cached = await ProcessAsync(provider, recipientId, withoutManifest);
        await RemoveFastPathEntryAsync(application, idempotencyKey);
        KafkaDisposition stored = await ProcessAsync(provider, recipientId, withoutManifest);

        accepted.ShouldBeOfType<KafkaDisposition.Processed>();
        republished.ShouldBeOfType<KafkaDisposition.Duplicate>();
        cached.ShouldBeOfType<KafkaDisposition.DeadLetter>()
            .Reason.ShouldBe(NotificationRejectionReasons.IdempotencyKeyConflict);
        stored.ShouldBeOfType<KafkaDisposition.DeadLetter>()
            .Reason.ShouldBe(NotificationRejectionReasons.IdempotencyKeyConflict);

        // The refused attempts left nothing behind: the key still answers for
        // the one notification the accepted request created.
        (await NotificationCountAsync(application, idempotencyKey)).ShouldBe(1);
    }

    /// <summary>
    /// A manifest the ingestion cannot accept never reaches the identity of a
    /// request, the registration that persists it, or the acceptance the
    /// producer would read as a delivery. The empty list is here because a
    /// producer asking for attachments and naming none is a different request
    /// from one that never asked, and the shapes that are not a list of
    /// references are here because a member of the contract whose JSON cannot
    /// carry it is a malformed body.
    /// </summary>
    [RequiresDockerTheory]
    [InlineData("[]")]
    [InlineData("""[""]""")]
    [InlineData("""["   "]""")]
    [InlineData("""["att_alpha", "att_alpha"]""")]
    [InlineData("\"att_alpha\"")]
    [InlineData("7")]
    [InlineData("[7]")]
    [InlineData("{}")]
    public async Task A_manifest_the_ingestion_cannot_accept_never_becomes_a_notification(
        string manifestJson)
    {
        var application = KafkaIngressApi.NewApplication();
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();
        var body = WithManifest(
            Event(application, "template-is-never-read", recipientId, idempotencyKey),
            manifestJson);

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        KafkaDisposition disposition = await ProcessAsync(provider, recipientId, body);

        disposition.ShouldBeOfType<KafkaDisposition.DeadLetter>()
            .Reason.ShouldBe(NotificationRejectionReasons.PayloadInvalid);
        await AssertNothingWasAcceptedAsync(application, idempotencyKey, recipientId);
    }

    /// <summary>
    /// The envelope type is checked before anything binds the body, and a
    /// manifest does not move that. The body below carries a manifest the
    /// ingestion would refuse, so an answer of payload-invalid would mean the
    /// binder ran ahead of the type and a later version of this contract had
    /// been processed as if the producer had spoken this one.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_manifest_published_under_another_envelope_type_is_refused_before_the_body_binds()
    {
        var application = KafkaIngressApi.NewApplication();
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();
        var unsupported = WithManifest(
            Event(
                application,
                "template-is-never-read",
                recipientId,
                idempotencyKey,
                eventType: "araia.notification.requested.v2"),
            "[]");

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        KafkaDisposition disposition = await ProcessAsync(provider, recipientId, unsupported);

        disposition.ShouldBeOfType<KafkaDisposition.DeadLetter>()
            .Reason.ShouldBe(NotificationRejectionReasons.EventTypeUnsupported);

        // Falsification: the same body under the declared type reaches the
        // binder and is refused for the manifest it carries, so the refusal
        // above belongs to the type and not to the manifest.
        var declared = WithManifest(
            Event(application, "template-is-never-read", recipientId, idempotencyKey),
            "[]");
        KafkaDisposition bound = await ProcessAsync(provider, recipientId, declared);

        bound.ShouldBeOfType<KafkaDisposition.DeadLetter>()
            .Reason.ShouldBe(NotificationRejectionReasons.PayloadInvalid);
        await AssertNothingWasAcceptedAsync(application, idempotencyKey, recipientId);
    }

    /// <summary>
    /// Shape answers before trust, and a manifest does not move that either. A
    /// request whose manifest the ingestion cannot accept is refused for what
    /// it is even when its producer would also fail authorization, which is
    /// what keeps an unreadable request from being answered by a reason that
    /// describes someone else.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_manifest_the_ingestion_cannot_accept_is_refused_for_its_shape_even_when_the_producer_is_unknown()
    {
        var application = KafkaIngressApi.NewApplication();
        await fixture.SeedProducerGrantsAsync(("someone-else", application, "transactional"));
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();
        var body = WithManifest(
            Event(application, "template-is-never-read", recipientId, idempotencyKey),
            """["att_alpha", "att_alpha"]""");

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        KafkaDisposition disposition = await IngressRecords.ProcessAsync(
            fixture, provider, recipientId, body, KafkaIngressApi.ProducerHeaders("intruder-service"));

        disposition.ShouldBeOfType<KafkaDisposition.DeadLetter>()
            .Reason.ShouldBe(NotificationRejectionReasons.PayloadInvalid);
        await AssertNothingWasAcceptedAsync(application, idempotencyKey, recipientId);
    }

    /// <summary>
    /// The topic is the trust binding, so a manifest published on the topic of
    /// a producer that holds no grant for the application asks for nothing.
    /// The falsifying half publishes the same body on the bound topic, so the
    /// refusal belongs to the topic and not to the manifest.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_manifest_published_on_the_topic_of_a_producer_without_a_grant_never_becomes_a_notification()
    {
        var application = KafkaIngressApi.NewApplication();
        (var templateKey, _) =
            await KafkaIngressApi.CreatePublishedTemplateAsync(fixture, application, "transactional");
        await fixture.SeedProducerGrantsAsync((Producer, application, "transactional"));
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var foreignKey = KafkaIngressApi.NewIdempotencyKey();
        var boundKey = KafkaIngressApi.NewIdempotencyKey();
        SeededAttachment attachment = await ClaimableAttachments.ReleasedAsync(fixture, application);

        await using ServiceProvider provider = fixture.BuildIngressProvider();
        KafkaDisposition foreignTopic = await IngressRecords.ProcessAsync(
            fixture,
            provider,
            recipientId,
            WithManifest(
                Event(application, templateKey, recipientId, foreignKey),
                Manifest(attachment.Reference)),
            KafkaIngressApi.ProducerHeaders(Producer),
            KafkaIngressFixture.SecondaryRequestedTopic);
        KafkaDisposition boundTopic = await ProcessAsync(
            provider,
            recipientId,
            WithManifest(
                Event(application, templateKey, recipientId, boundKey),
                Manifest(attachment.Reference)));

        foreignTopic.ShouldBeOfType<KafkaDisposition.DeadLetter>()
            .Reason.ShouldBe(NotificationRejectionReasons.ProducerNotAuthorized);
        boundTopic.ShouldBeOfType<KafkaDisposition.Processed>();
        (await NotificationCountAsync(application, foreignKey)).ShouldBe(0);
        (await NotificationCountAsync(application, boundKey)).ShouldBe(1);
    }

    /// <summary>
    /// A refused manifest leaves no reference on a surface that does not carry
    /// attachments. The reference is a value the producer chose, and the
    /// dead-letter topic, the outgoing event and the audit trail all outlive
    /// the request that was refused.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_refused_manifest_leaves_no_reference_on_the_surfaces_that_do_not_carry_attachments()
    {
        var application = KafkaIngressApi.NewApplication();
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var idempotencyKey = KafkaIngressApi.NewIdempotencyKey();

        // Unique on purpose: a reference that could appear for another reason
        // would let this pass over a surface that really carries it.
        var reference = $"att_{Guid.NewGuid():N}";
        var body = WithManifest(
            Event(application, "template-is-never-read", recipientId, idempotencyKey),
            JsonSerializer.Serialize(new[] { reference, reference }));

        // The premise, asserted rather than assumed: the reference is in the
        // bytes the producer published. Without it this would clear a record
        // that never carried the value and would pass either way.
        body.Contains(reference, StringComparison.Ordinal)
            .ShouldBeTrue("O corpo publicado deve carregar a referência do manifesto.");

        Dictionary<string, string> headers = KafkaIngressApi.ProducerHeaders(Producer);
        TopicPartitionOffset position = await fixture.ProduceAsync(
            KafkaIngressFixture.RequestedTopic, recipientId, body, headers);
        await using ServiceProvider provider = fixture.BuildIngressProvider();
        using IServiceScope scope = provider.CreateScope();
        KafkaDisposition disposition = await scope.ServiceProvider
            .GetRequiredService<KafkaIngressProcessor>()
            .ProcessAsync(
                IngressRecords.Context(position, recipientId, body, headers),
                CancellationToken.None);

        disposition.ShouldBeOfType<KafkaDisposition.DeadLetter>()
            .Reason.ShouldBe(NotificationRejectionReasons.PayloadInvalid);

        // The record is found by the coordinates the broker assigned, because
        // a refusal before producer trust rebuilds the body from the
        // diagnostic allow-list and nothing of the request survives in it.
        ConsumeResult<string, byte[]> deadLetter = await ReadDeadLetterAsync(position);
        IngressRecords.Body(deadLetter).ShouldNotContain(reference);

        List<string> rejectionPayloads = await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .Where(message => message.MessageKey == recipientId)
            .Select(message => message.PayloadJson)
            .ToListAsync());
        rejectionPayloads.ShouldNotBeEmpty();
        foreach (var payload in rejectionPayloads)
        {
            payload.ShouldNotContain(reference);
        }

        var auditDetails = await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .Where(entry => entry.EntityId == $"{application}:{idempotencyKey}")
            .Select(entry => entry.DetailsJson)
            .SingleAsync());
        auditDetails.ShouldNotContain(reference);
    }

    /// <summary>
    /// A deployment that takes no new attachments says exactly that, and the
    /// dead-letter record carries the word instead of the one every set that
    /// may not be had gets. The two ask a producer for opposite things: wait
    /// for whoever runs the service to switch the capability on, or stop
    /// sending this set. A record that carried the generic word would send a
    /// producer looking for a defect in references that have none.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_manifest_published_to_a_deployment_that_takes_no_attachments_is_told_so()
    {
        var application = KafkaIngressApi.NewApplication();
        (var templateKey, _) =
            await KafkaIngressApi.CreatePublishedTemplateAsync(fixture, application, "transactional");
        await fixture.SeedProducerGrantsAsync((Producer, application, "transactional"));
        var recipientId = $"cus_{Guid.NewGuid():N}";
        var closedKey = KafkaIngressApi.NewIdempotencyKey();
        var openKey = KafkaIngressApi.NewIdempotencyKey();
        SeededAttachment attachment = await ClaimableAttachments.ReleasedAsync(fixture, application);

        await using ServiceProvider closed = fixture.BuildIngressProvider(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Modules:AttachmentManagement:Capability:AcceptsNewAttachments"] = "false",
            });
        KafkaDisposition refused = await ProcessAsync(
            closed,
            recipientId,
            WithManifest(
                Event(application, templateKey, recipientId, closedKey),
                Manifest(attachment.Reference)));

        refused.ShouldBeOfType<KafkaDisposition.DeadLetter>()
            .Reason.ShouldBe("attachment-capability-not-enabled");
        await AssertNothingWasAcceptedAsync(application, closedKey, recipientId);

        // The falsifying half: the same released reference, the same producer
        // and the same shape of body, over a host that differs in the switch
        // and in nothing else. Without it the refusal above would be satisfied
        // by an attachment that was never claimable to begin with.
        await using ServiceProvider open = fixture.BuildIngressProvider();
        KafkaDisposition accepted = await ProcessAsync(
            open,
            recipientId,
            WithManifest(
                Event(application, templateKey, recipientId, openKey),
                Manifest(attachment.Reference)));

        accepted.ShouldBeOfType<KafkaDisposition.Processed>();
        (await NotificationCountAsync(application, openKey)).ShouldBe(1);
    }

    /// <summary>
    /// The dead-letter record of one published event, read back from the
    /// topic. The read is repeated rather than budgeted once: each read joins
    /// a consumer group of its own, and a join slower than the idle cutoff
    /// returns an empty read of a topic that already holds the record. The
    /// record itself is durable before this runs, because the refusal awaited
    /// its delivery report.
    /// </summary>
    private async Task<ConsumeResult<string, byte[]>> ReadDeadLetterAsync(
        TopicPartitionOffset position)
    {
        for (var attempt = 0; attempt < ReadAttempts; attempt++)
        {
            ConsumeResult<string, byte[]>? record = fixture
                .ReadAll(KafkaIngressFixture.DeadLetterTopic, ReadBudget)
                .SingleOrDefault(candidate => IsDeadLetterFor(candidate, position));
            if (record is not null)
            {
                return record;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new InvalidOperationException(
            $"Nenhum registro de dead-letter foi lido para as coordenadas {position}.");
    }

    private static bool IsDeadLetterFor(
        ConsumeResult<string, byte[]> record,
        TopicPartitionOffset position)
        => IngressRecords.Header(record, DeadLetterHeaders.SourceTopic) == position.Topic
            && IngressRecords.Header(record, DeadLetterHeaders.SourcePartition)
                == position.Partition.Value.ToString(CultureInfo.InvariantCulture)
            && IngressRecords.Header(record, DeadLetterHeaders.SourceOffset)
                == position.Offset.Value.ToString(CultureInfo.InvariantCulture);

    private Task<KafkaDisposition> ProcessAsync(
        ServiceProvider provider,
        string recipientId,
        string body)
        => IngressRecords.ProcessAsync(
            fixture, provider, recipientId, body, KafkaIngressApi.ProducerHeaders(Producer));

    private async Task AssertNothingWasAcceptedAsync(
        string application,
        string idempotencyKey,
        string recipientId)
    {
        (await NotificationCountAsync(application, idempotencyKey)).ShouldBe(0);
        (await fixture.QueryNotificationsDbAsync(db => db.IdempotencyRegistrations
            .AsNoTracking()
            .AnyAsync(registration => registration.Application == application
                && registration.IdempotencyKey == idempotencyKey)))
            .ShouldBeFalse();
        (await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .AnyAsync(message => message.MessageKey == recipientId
                && message.EventType == AcceptedEventType)))
            .ShouldBeFalse();
    }

    private Task<int> NotificationCountAsync(string application, string idempotencyKey)
        => fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .CountAsync(notification => notification.Application == application
                && notification.IdempotencyKey == idempotencyKey));

    private async Task RemoveFastPathEntryAsync(string application, string idempotencyKey)
    {
        var options = ConfigurationOptions.Parse(fixture.RedisConnectionString);
        options.AbortOnConnectFail = false;
        await using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(options);
        await connection.GetDatabase().KeyDeleteAsync(
            $"{KafkaIngressFixture.RedisKeyPrefix}idem:{application}:{idempotencyKey}");
    }

    private static string Event(
        string application,
        string templateKey,
        string recipientId,
        string idempotencyKey,
        string? eventType = null)
        => KafkaIngressApi.RequestedEvent(
            application,
            templateKey,
            "transactional",
            recipientId,
            idempotencyKey,
            eventType is null
                ? null
                : new KafkaIngressApi.RequestedEventOptions { EventType = eventType });

    /// <summary>
    /// Splices the manifest into the published body as raw JSON text. Raw on
    /// purpose: the cases here are about JSON shapes a typed builder could not
    /// express, and a body assembled from objects would only ever produce the
    /// shapes that already bind.
    /// </summary>
    /// <summary>The manifest member as a published body carries it.</summary>
    private static string Manifest(params string[] references)
        => JsonSerializer.Serialize(references);

    private static string WithManifest(string body, string manifestJson)
    {
        JsonObject envelope = JsonNode.Parse(body)?.AsObject()
            ?? throw new InvalidOperationException("O evento de teste deve ser um objeto JSON.");
        JsonObject data = envelope["data"]?.AsObject()
            ?? throw new InvalidOperationException("O evento de teste deve conter data.");
        data["attachments"] = JsonNode.Parse(manifestJson);
        return envelope.ToJsonString();
    }
}
