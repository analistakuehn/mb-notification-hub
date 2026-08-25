using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Webhooks;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Authentication;
using NotificationHub.IntegrationTests.Dispatching;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications.DeliveryTracking;

[Collection(DeliveryTrackingCollectionDefinition.Name)]
public sealed class ProviderWebhookIngestionTests(DeliveryTrackingFixture fixture)
{
    [RequiresDockerFact]
    public async Task An_altered_signature_stores_nothing_and_leaves_a_security_record()
    {
        var eventId = NewEventId();
        var capturing = new CapturingLoggerProvider();
        using WebApplicationFactory<Program> observed = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(capturing)));
        HttpClient client = observed.CreateClient();

        HttpResponseMessage response = await client.SendAsync(DeliveryTrackingApi.SendGridCallback(
            fixture, DeliveryEventBatch.Of(eventId, "msg-altered", "delivered"), tamper: true));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await DeliveryTrackingApi.CountEvidenceAsync(fixture, eventId)).ShouldBe(0);
        (await DeliveryTrackingApi.CountDedupeAsync(
            fixture, DeliveryTrackingApi.SendGridProvider, eventId)).ShouldBe(0);
        capturing.Lines.ShouldContain(
            line => line.Contains("recusado", StringComparison.Ordinal)
                && line.Contains(DeliveryTrackingApi.SendGridProvider, StringComparison.Ordinal),
            "uma assinatura recusada precisa deixar registro de segurança nomeando o provedor.");
    }

    [RequiresDockerFact]
    public async Task An_origin_outside_the_allowlist_leaves_its_own_security_record()
    {
        var eventId = NewEventId();
        var capturing = new CapturingLoggerProvider();

        // A prefix the loopback caller can never match, so the allowlist is
        // the only gate that refuses this authentic, correctly signed call.
        using WebApplicationFactory<Program> pinned = fixture.WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.AddProvider(capturing));
            builder.UseSetting("Modules:Dispatch:Webhooks:SendGrid:AllowedNetworks:0", "198.51.100.0/24");
        });
        HttpClient client = pinned.CreateClient();

        HttpResponseMessage response = await client.SendAsync(DeliveryTrackingApi.SendGridCallback(
            fixture, DeliveryEventBatch.Of(eventId, "msg-origin", "delivered")));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await DeliveryTrackingApi.CountEvidenceAsync(fixture, eventId)).ShouldBe(0);

        // Its own event, separable from an invalid signature: an address
        // outside the published range is forgery, while a bad signature is
        // also the everyday symptom of a rotated secret.
        capturing.Lines.ShouldContain(
            line => line.Contains("forjação", StringComparison.Ordinal),
            "origem fora da allowlist precisa gerar evento de segurança próprio, "
            + "distinto do de assinatura inválida.");
    }

    [RequiresDockerFact]
    public async Task The_same_provider_event_delivered_twice_produces_one_row_and_one_effect()
    {
        SeededAttempt seeded = await DeliveryTrackingApi.SeedAttemptAsync(
            fixture, "email", DeliveryTrackingApi.SendGridProvider, NewMessageId());
        var eventId = NewEventId();
        var body = DeliveryEventBatch.Of(eventId, NewMessageId(), "delivered", seeded);
        HttpClient client = fixture.CreateClient();

        HttpResponseMessage first = await client.SendAsync(
            DeliveryTrackingApi.SendGridCallback(fixture, body));
        HttpResponseMessage second = await client.SendAsync(
            DeliveryTrackingApi.SendGridCallback(fixture, body));

        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        second.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await DeliveryTrackingApi.CountEvidenceAsync(fixture, eventId)).ShouldBe(
            1,
            "a segunda entrega do mesmo identificador de evento não pode gravar outra evidência.");
        (await DeliveryTrackingApi.CountDedupeAsync(
            fixture, DeliveryTrackingApi.SendGridProvider, eventId)).ShouldBe(1);

        EvidenceRow evidence = await DeliveryTrackingApi.ReadEvidenceAsync(fixture, eventId);
        (await DeliveryTrackingApi.CountAnnouncedAsync(fixture, evidence.Id)).ShouldBe(
            1, "um único efeito significa uma única mensagem enfileirada.");
    }

    [RequiresDockerFact]
    public async Task The_receiving_transaction_writes_the_three_rows_and_never_touches_the_trail()
    {
        SeededAttempt seeded = await DeliveryTrackingApi.SeedAttemptAsync(
            fixture, "email", DeliveryTrackingApi.SendGridProvider, NewMessageId());
        var eventId = NewEventId();
        var probe = new RecordingAuditTrail();
        using WebApplicationFactory<Program> instrumented = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuditTrail>();
                services.AddSingleton<IAuditTrail>(probe);
            }));
        HttpClient client = instrumented.CreateClient();

        HttpResponseMessage response = await client.SendAsync(DeliveryTrackingApi.SendGridCallback(
            fixture, DeliveryEventBatch.Of(eventId, NewMessageId(), "delivered", seeded)));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        EvidenceRow evidence = await DeliveryTrackingApi.ReadEvidenceAsync(fixture, eventId);
        evidence.Kind.ShouldBe("delivered");
        (await DeliveryTrackingApi.CountDedupeAsync(
            fixture, DeliveryTrackingApi.SendGridProvider, eventId)).ShouldBe(1);
        (await DeliveryTrackingApi.CountAnnouncedAsync(fixture, evidence.Id)).ShouldBe(1);

        probe.Appends.ShouldBe(
            0,
            "o append de auditoria segura o lock da cadeia até o fim da transação e serializaria "
            + "o webhook contra a ingestão; a trilha pertence ao consumidor assíncrono.");
    }

    [RequiresDockerFact]
    public async Task The_stored_payload_is_sealed_and_never_readable_in_the_row()
    {
        SeededAttempt seeded = await DeliveryTrackingApi.SeedAttemptAsync(
            fixture, "email", DeliveryTrackingApi.SendGridProvider, NewMessageId());
        var eventId = NewEventId();
        const string address = "pessoa.destino@exemplo.test";
        HttpClient client = fixture.CreateClient();

        HttpResponseMessage response = await client.SendAsync(DeliveryTrackingApi.SendGridCallback(
            fixture, DeliveryEventBatch.Of(eventId, NewMessageId(), "delivered", seeded, address)));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        EvidenceRow evidence = await DeliveryTrackingApi.ReadEvidenceAsync(fixture, eventId);
        var stored = Encoding.UTF8.GetString(evidence.PayloadEncrypted);
        stored.Contains(address, StringComparison.Ordinal).ShouldBeFalse(
            "o corpo do provedor carrega contato em claro e o módulo proíbe PII em claro em repouso.");
    }

    [RequiresDockerFact]
    public async Task A_batch_carrying_no_tracked_event_answers_success_and_stores_nothing()
    {
        var eventId = NewEventId();
        HttpClient client = fixture.CreateClient();

        HttpResponseMessage response = await client.SendAsync(DeliveryTrackingApi.SendGridCallback(
            fixture, DeliveryEventBatch.Of(eventId, NewMessageId(), "click")));

        // A batch of engagement events alone is ordinary traffic. Answering an
        // error would buy an endless redelivery of a callback that never had
        // anything in it for this hub.
        ((int)response.StatusCode).ShouldBeInRange(200, 299);
        (await DeliveryTrackingApi.CountEvidenceAsync(fixture, eventId)).ShouldBe(0);
        (await DeliveryTrackingApi.CountDedupeAsync(
            fixture, DeliveryTrackingApi.SendGridProvider, eventId)).ShouldBe(0);
    }

    /// <summary>
    /// The one public route of this hub answers a batch it will not take,
    /// instead of taking as long as the caller asks it to.
    /// <para>
    /// Every event in a callback costs a transaction of its own, só the
    /// response time of this route is linear in a number chosen outside. The
    /// provider measures that time and redelivers whatever takes too long,
    /// which makes an unbounded batch a failure that feeds itself on the one
    /// surface nobody outside can be asked to slow down.
    /// </para>
    /// <para>
    /// The refusal is whole and not partial: answering success over events this
    /// hub never stored would be worse than refusing, because the provider
    /// would stop resending them.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task A_callback_over_the_event_ceiling_is_refused_whole_and_stores_nothing()
    {
        var eventId = NewEventId();
        using WebApplicationFactory<Program> narrow = fixture.WithWebHostBuilder(builder =>
            builder.UseSetting(
                $"{ProviderWebhookIngestionOptions.SectionName}:MaxEventsPerCallback", "2"));
        HttpClient client = narrow.CreateClient();

        HttpResponseMessage response = await client.SendAsync(DeliveryTrackingApi.SendGridCallback(
            fixture,
            DeliveryEventBatch.Many(eventId, NewMessageId(), "delivered", count: 3)));

        response.StatusCode.ShouldBe(
            HttpStatusCode.RequestEntityTooLarge,
            "um lote acima do teto tem status próprio, para que o operador o distinga de um "
            + "payload que o adaptador não soube ler.");
        (await DeliveryTrackingApi.CountEvidenceAsync(fixture, $"{eventId}-0")).ShouldBe(0);
        (await DeliveryTrackingApi.CountEvidenceAsync(fixture, $"{eventId}-2")).ShouldBe(0);
    }

    [RequiresDockerFact]
    public async Task A_signature_over_the_internal_address_is_refused_when_a_public_base_is_configured()
    {
        var messageSid = $"SM{Guid.NewGuid():N}";
        await DeliveryTrackingApi.SeedAttemptAsync(
            fixture, "sms", DeliveryTrackingApi.TwilioProvider, messageSid);
        List<KeyValuePair<string, string>> form =
        [
            new("MessageSid", messageSid),
            new("MessageStatus", "delivered"),
        ];
        using WebApplicationFactory<Program> behindProxy = fixture.WithWebHostBuilder(builder =>
            builder.UseSetting(
                $"{ProviderSignatureDefaults.SectionName}:PublicBaseUrl",
                DeliveryTrackingFixture.PublicBaseUrl));
        HttpClient client = behindProxy.CreateClient();

        // Signed over the address this process observes, which behind a
        // balancer is not the address the provider was given.
        HttpResponseMessage internalSignature = await client.SendAsync(
            DeliveryTrackingApi.TwilioCallback(form));

        // The same call signed over the published address.
        HttpResponseMessage publicSignature = await client.SendAsync(
            DeliveryTrackingApi.TwilioCallback(
                form, signedBaseUrl: DeliveryTrackingFixture.PublicBaseUrl));

        internalSignature.StatusCode.ShouldBe(
            HttpStatusCode.Unauthorized,
            "a assinatura da Twilio cobre a URL completa, e a base observada não é a base assinada.");
        publicSignature.StatusCode.ShouldBe(
            HttpStatusCode.Accepted,
            "sem base pública configurável toda assinatura válida seria recusada em produção "
            + "e ainda assim passaria em teste.");
    }

    [RequiresDockerFact]
    public async Task A_callback_addressed_to_a_provider_this_process_does_not_speak_for_is_refused()
    {
        HttpClient client = fixture.CreateClient();

        HttpResponseMessage response = await client.PostAsync(
            "/webhooks/a-provider-nobody-hosts",
            new StringContent("[]", Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [RequiresDockerFact]
    public async Task The_ingestion_stores_the_evidence_without_moving_the_attempt()
    {
        SeededAttempt seeded = await DeliveryTrackingApi.SeedAttemptAsync(
            fixture,
            "email",
            DeliveryTrackingApi.SendGridProvider,
            providerMessageId: null,
            status: NotificationAttemptStatuses.Queued);
        var eventId = NewEventId();
        HttpClient client = fixture.CreateClient();

        HttpResponseMessage response = await client.SendAsync(DeliveryTrackingApi.SendGridCallback(
            fixture, DeliveryEventBatch.Of(eventId, NewMessageId(), "delivered", seeded)));

        // The request stores evidence and answers; nothing about the attempt
        // changes inside it, because the state machine runs off the request.
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await DeliveryTrackingApi.ReadAttemptStatusAsync(fixture, seeded.AttemptId))
            .ShouldBe(NotificationAttemptStatuses.Queued);
        (await DeliveryTrackingApi.ReadEvidenceAsync(fixture, eventId)).AppliedAt.ShouldBeNull();
    }

    /// <summary>
    /// This provider signs the timestamp and the body, never the address, so a
    /// pair of identifiers arriving in the query is an unsigned claim about
    /// which attempt an authentic callback describes. Honouring it would let a
    /// genuine callback, captured or merely replayed with a different query, be
    /// steered onto an attempt it says nothing about, moving that attempt's
    /// state and with it its fallback and its suppression effects.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_signed_callback_without_correlation_cannot_be_steered_by_the_query()
    {
        SeededAttempt target = await DeliveryTrackingApi.SeedAttemptAsync(
            fixture,
            "email",
            DeliveryTrackingApi.SendGridProvider,
            providerMessageId: null,
            status: NotificationAttemptStatuses.Sent);
        var eventId = NewEventId();
        HttpClient client = fixture.CreateClient();

        // A validly signed body that carries no correlation of its own, sent to
        // an address that names someone else's attempt.
        HttpResponseMessage response = await client.SendAsync(DeliveryTrackingApi.SendGridCallback(
            fixture,
            DeliveryEventBatch.Of(eventId, NewMessageId(), "delivered"),
            query: $"?notificationId={target.NotificationId}&attemptId={target.AttemptId}"));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        EvidenceRow evidence = await DeliveryTrackingApi.ReadEvidenceAsync(fixture, eventId);
        evidence.AttemptId.ShouldBeNull(
            "a correlação veio da rota, que a assinatura deste provedor não cobre: "
            + "aceitá-la deixaria um callback autêntico decidir sobre um attempt "
            + "sobre o qual ele nada diz.");
        evidence.NotificationId.ShouldBeNull();
        (await DeliveryTrackingApi.ReadAttemptStatusAsync(fixture, target.AttemptId))
            .ShouldBe(NotificationAttemptStatuses.Sent);
    }

    private static string NewEventId() => $"evt-{Guid.NewGuid():N}";

    private static string NewMessageId() => $"msg-{Guid.NewGuid():N}";
}

/// <summary>
/// Builds the body of one SendGrid event callback. The shape is the
/// provider's, so the batch is written as text rather than serialized from a
/// model: the signature covers these exact octets, and a serializer that
/// reorders or re-encodes them would be signing something else.
/// </summary>
internal static class DeliveryEventBatch
{
    internal static string Of(
        string eventId,
        string messageId,
        string eventName,
        SeededAttempt? seeded = null,
        string? address = null,
        long? occurredAt = null)
    {
        var body = new StringBuilder("[{");
        body.Append(CultureInfo.InvariantCulture, $"\"sg_event_id\":\"{eventId}\",");
        body.Append(CultureInfo.InvariantCulture, $"\"sg_message_id\":\"{messageId}\",");
        body.Append(CultureInfo.InvariantCulture, $"\"event\":\"{eventName}\",");
        body.Append(
            CultureInfo.InvariantCulture,
            $"\"timestamp\":{occurredAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
        if (seeded is not null)
        {
            body.Append(
                CultureInfo.InvariantCulture,
                $",\"notification_id\":\"{seeded.NotificationId}\"");
            body.Append(CultureInfo.InvariantCulture, $",\"attempt_id\":\"{seeded.AttemptId}\"");
        }

        if (address is not null) body.Append(CultureInfo.InvariantCulture, $",\"email\":\"{address}\"");

        return body.Append("}]").ToString();
    }

    /// <summary>One bounce entry, whose type is what decides the suppression signal.</summary>
    internal static string Bounce(string eventId, string messageId, SeededAttempt? seeded, string type)
    {
        var body = new StringBuilder("[{");
        body.Append(CultureInfo.InvariantCulture, $"\"sg_event_id\":\"{eventId}\",");
        body.Append(CultureInfo.InvariantCulture, $"\"sg_message_id\":\"{messageId}\",");
        body.Append(CultureInfo.InvariantCulture, $"\"event\":\"bounce\",\"type\":\"{type}\",");
        body.Append(CultureInfo.InvariantCulture, $"\"status\":\"5.1.1\",");
        body.Append(
            CultureInfo.InvariantCulture,
            $"\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
        if (seeded is not null)
        {
            body.Append(
                CultureInfo.InvariantCulture,
                $",\"notification_id\":\"{seeded.NotificationId}\"");
            body.Append(CultureInfo.InvariantCulture, $",\"attempt_id\":\"{seeded.AttemptId}\"");
        }

        return body.Append("}]").ToString();
    }

    /// <summary>
    /// A batch of the requested size, with one identity per entry. It exists to
    /// exercise the ceiling, which is a property of how many events a callback
    /// carries and not of how many bytes it weighs.
    /// </summary>
    internal static string Many(string eventId, string messageId, string eventName, int count)
    {
        var body = new StringBuilder("[");
        for (var index = 0; index < count; index++)
        {
            if (index > 0) body.Append(',');
            body.Append('{');
            body.Append(CultureInfo.InvariantCulture, $"\"sg_event_id\":\"{eventId}-{index}\",");
            body.Append(CultureInfo.InvariantCulture, $"\"sg_message_id\":\"{messageId}\",");
            body.Append(CultureInfo.InvariantCulture, $"\"event\":\"{eventName}\",");
            body.Append(
                CultureInfo.InvariantCulture,
                $"\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
            body.Append('}');
        }

        return body.Append(']').ToString();
    }
}
