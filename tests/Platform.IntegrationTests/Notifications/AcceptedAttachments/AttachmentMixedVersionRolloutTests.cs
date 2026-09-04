using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Capability;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.IntegrationTests.AttachmentManagement;
using NotificationHub.IntegrationTests.Dispatching;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications.AcceptedAttachments;

/// <summary>
/// The deployment matrix run against one database by two hosts of the same
/// build: the one that takes new attachments and the one that does not.
/// <para>
/// The combination that must never happen is a reader with no notion of the
/// snapshot meeting a row that carries one, and what keeps it from happening
/// is the switch: while it is off no acceptance writes a document at all. That
/// implication is measured here end to end rather than argued, by looking for
/// the set the request named inside every stored document of the table and
/// finding it exactly where the accepting host put it and nowhere else. The
/// other half, deploying readers before writers, is an order of operations and
/// lives in the runbook, not in code.
/// </para>
/// <para>
/// Beside it, the two properties a rollout is most likely to break by
/// accident: a retry of an already accepted request must still be answered
/// with the acceptance it already got, whichever host receives it, and the
/// path that names no attachment must hash exactly as it did before the member
/// existed.
/// </para>
/// </summary>
[Collection(AcceptedAttachmentFlowCollectionDefinition.Name)]
public sealed partial class AttachmentMixedVersionRolloutTests(AcceptedAttachmentFlowFixture fixture)
{
    private const string CapabilityRefused = "attachment-capability-not-enabled";

    private const string VariableCode = "123456";

    private const int Ttl = 300;

    private static readonly (string Channel, string? Timeout)[] EmailOnly = [("email", null)];

    /// <summary>
    /// The switch, named rather than inherited: a host that relied on the
    /// silence of its settings would be measuring an omission instead of the
    /// state the rehearsal claims.
    /// </summary>
    private static Dictionary<string, string?> CapabilityOff() => new(StringComparer.Ordinal)
    {
        [$"{AttachmentCapabilityOptions.SectionName}:AcceptsNewAttachments"] = "false",
    };

    /// <summary>
    /// A request that names attachments, sent to the host that takes them and
    /// to the host that does not, over one arrangement and one database. The
    /// accepting host leaves a document naming its set; the closed host leaves
    /// no row and no document anywhere.
    /// <para>
    /// The scan is over the whole table and looks for the reference inside the
    /// stored documents, so the answer for the closed host is not "the row I
    /// looked at is empty" but "the set this request named is in no document
    /// at all". The accepting host is in the same measurement on purpose: it
    /// is the tripwire that says the scan can find a set when one is there,
    /// without which the zero below would be the answer of a scan that finds
    /// nothing ever.
    /// </para>
    /// <para>
    /// It is also what stands in for the retired clause about a contract
    /// version that refused attachments. There is one version, it accepts
    /// them, and the property worth holding is this one: a body that names a
    /// manifest is never accepted unless the manifest was carried onto the
    /// row.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task A_body_that_names_a_manifest_is_accepted_only_where_the_manifest_is_carried()
    {
        AttachmentArrangement arrangement = await AcceptedAttachmentFlow.ArrangeAsync(
            fixture, EmailOnly);
        SeededAttachment carried = await ClaimableAttachments.ReleasedWithContentAsync(
            fixture, arrangement.Application);
        SeededAttachment refused = await ClaimableAttachments.ReleasedWithContentAsync(
            fixture, arrangement.Application);

        (var openRecipient, _, _) = await DispatchApi.RegisterRecipientAsync(fixture);
        using HttpClient accepting = fixture.CreateProducerClient(
            "mixed-version-open", NotificationsApi.SendTransactional);
        using HttpResponseMessage opened = await NotificationsApi.PostNotificationAsync(
            accepting,
            Body(arrangement, openRecipient, [carried.Reference]),
            Guid.NewGuid().ToString("N"));
        var openedBody = await opened.Content.ReadAsStringAsync();

        (var closedRecipient, _, _) = await DispatchApi.RegisterRecipientAsync(fixture);
        using WebApplicationFactory<Program> closed = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(CapabilityOff())));
        using HttpClient blocked = fixture.CreateProducerClient(
            closed, "mixed-version-closed", NotificationsApi.SendTransactional);
        using HttpResponseMessage held = await NotificationsApi.PostNotificationAsync(
            blocked,
            Body(arrangement, closedRecipient, [refused.Reference]),
            Guid.NewGuid().ToString("N"));
        var heldBody = await held.Content.ReadAsStringAsync();

        // The harm first: whether the set the closed host was asked for ended
        // up in a durable document. Asserting the status code before this
        // would hide the scan behind the refusal it is there to corroborate.
        (await DocumentsNamingAsync(refused.Reference)).ShouldBe(
            0,
            "com a chave desligada nenhum escritor pode deixar documento não nulo, e a "
            + "combinação proibida deixa de existir por essa implicação.");
        (await DocumentsNamingAsync(carried.Reference)).ShouldBe(
            1,
            "sem o braço que encontra um conjunto gravado, o zero acima seria a resposta de "
            + "uma varredura que nunca encontra nada.");

        opened.StatusCode.ShouldBe(HttpStatusCode.Accepted, openedBody);
        held.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, heldBody);
        heldBody.ShouldContain(CapabilityRefused);

        // The accepted row carries the manifest it named, and the refused one
        // left no notification behind to carry anything.
        Guid accepted = await NotificationIdOfAsync(openRecipient);
        (await AcceptedAttachmentFlow.StoredSetAsync(fixture, accepted))
            .Select(item => item.Reference)
            .ShouldBe([carried.Reference]);
        (await NotificationCountOfAsync(closedRecipient)).ShouldBe(0);
    }

    /// <summary>
    /// A retry of a request that was already accepted with a set, sent to a
    /// host that no longer takes new attachments. It is answered with the
    /// acceptance it already had.
    /// <para>
    /// This is the accident a reversal invites: the producer is repeating a
    /// request the hub already agreed to, and the switch was turned off after
    /// that agreement. Refusing the retry would tell the producer that a
    /// delivery it was promised is not going to happen, and no retry policy
    /// recovers from that answer. The set is claimed once, and the document on
    /// the row is compared byte for byte across the two calls.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task A_retry_of_an_accepted_set_is_answered_again_by_a_host_that_takes_no_new_attachments()
    {
        AttachmentArrangement arrangement = await AcceptedAttachmentFlow.ArrangeAsync(
            fixture, EmailOnly);
        SeededAttachment attachment = await ClaimableAttachments.ReleasedWithContentAsync(
            fixture, arrangement.Application);
        (var recipientId, _, _) = await DispatchApi.RegisterRecipientAsync(fixture);
        Dictionary<string, object?> body = Body(arrangement, recipientId, [attachment.Reference]);
        var key = Guid.NewGuid().ToString("N");

        using HttpClient accepting = fixture.CreateProducerClient(
            "mixed-version-retry-open", NotificationsApi.SendTransactional);
        using HttpResponseMessage first = await NotificationsApi.PostNotificationAsync(
            accepting, body, key);
        var firstBody = await first.Content.ReadAsStringAsync();
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted, firstBody);
        Guid accepted = await IdOfAsync(first);
        var frozen = await AcceptedAttachmentFlow.StoredDocumentAsync(fixture, accepted);
        frozen.ShouldNotBeNull();

        using WebApplicationFactory<Program> closed = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(CapabilityOff())));
        using HttpClient blocked = fixture.CreateProducerClient(
            closed, "mixed-version-retry-closed", NotificationsApi.SendTransactional);
        using HttpResponseMessage again = await NotificationsApi.PostNotificationAsync(
            blocked, body, key);
        var againBody = await again.Content.ReadAsStringAsync();

        // The answer of a repetition, which is not the answer of an
        // acceptance: the endpoint says 200 for a request it has already
        // decided and 202 for one it is deciding now, and the retry has to get
        // the first of the two rather than any refusal.
        again.StatusCode.ShouldBe(HttpStatusCode.OK, againBody);
        (await IdOfAsync(again)).ShouldBe(accepted);

        // One acceptance, one claim, one document: the retry answered from the
        // registration and never re-entered the path the switch closes.
        (await AcceptedAttachmentFlow.StoredDocumentAsync(fixture, accepted)).ShouldBe(frozen);
        (await ClaimableAttachments.HoldCountAsync(fixture, attachment.Id)).ShouldBe(1);
        (await NotificationCountOfAsync(recipientId)).ShouldBe(1);
    }

    /// <summary>
    /// The path that names no attachment, over both hosts, compared against
    /// the canonical bytes written out here rather than against whatever the
    /// production writer produces today.
    /// <para>
    /// The digest the hub stores is what decides whether a retry is a
    /// repetition or a conflict, so it is the value a producer that never
    /// heard of attachments depends on. The expected form is spelled out as
    /// text, with no member for the manifest, and hashed in the test: a
    /// comparison against the same function that computes it would agree with
    /// any change to that function, including the one that adds an empty
    /// member and turns every retry of every existing producer into a
    /// conflict.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task A_request_that_names_no_attachment_hashes_as_the_form_without_the_member()
    {
        AttachmentArrangement arrangement = await AcceptedAttachmentFlow.ArrangeAsync(
            fixture, EmailOnly);
        using WebApplicationFactory<Program> closed = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(CapabilityOff())));
        using HttpClient accepting = fixture.CreateProducerClient(
            "mixed-version-plain-open", NotificationsApi.SendTransactional);
        using HttpClient blocked = fixture.CreateProducerClient(
            closed, "mixed-version-plain-closed", NotificationsApi.SendTransactional);

        var divergences = new List<string>();
        foreach ((var host, HttpClient producer) in new[]
        {
            ("aceita anexos", accepting),
            ("não aceita anexos", blocked),
        })
        {
            (var recipientId, _, _) = await DispatchApi.RegisterRecipientAsync(fixture);
            var key = Guid.NewGuid().ToString("N");
            using HttpResponseMessage answer = await NotificationsApi.PostNotificationAsync(
                producer, Body(arrangement, recipientId, []), key);
            var answered = await answer.Content.ReadAsStringAsync();
            answer.StatusCode.ShouldBe(HttpStatusCode.Accepted, answered);

            var expected = DigestOf(CanonicalWithoutTheMember(arrangement, recipientId));
            var stored = await StoredHashAsync(arrangement.Application, key);
            if (!string.Equals(expected, stored, StringComparison.Ordinal))
            {
                divergences.Add($"{host}: esperado {expected}, medido {stored}");
            }

            // The row of the same request, so the identity of the path is not
            // only its digest: a notification that named nothing carries
            // nothing, and the column stays empty rather than holding a
            // document that describes an empty set.
            (await AcceptedAttachmentFlow.StoredDocumentAsync(fixture, await IdOfAsync(answer)))
                .ShouldBeNull();
        }

        divergences.ShouldBeEmpty(string.Join("; ", divergences));
    }

    /// <summary>
    /// The canonical form of a request that names no attachment, written out
    /// as text. The members are in the fixed order the form declares, the
    /// locale is absent because it reaches no decision, and there is no member
    /// for the manifest at all.
    /// </summary>
    private static string CanonicalWithoutTheMember(
        AttachmentArrangement arrangement,
        string recipientId)
    {
        // Every value interpolated below goes into a JSON string, so the
        // literal is only the canonical form if none of them needs escaping.
        foreach (var value in new[]
        {
            arrangement.Application, arrangement.TemplateKey, recipientId,
        })
        {
            SafeInJsonText().IsMatch(value).ShouldBeTrue(value);
        }

        return $$$"""
            {"application":"{{{arrangement.Application}}}","class":"{{{AcceptedAttachmentFlow.NotificationClass}}}","recipientId":"{{{recipientId}}}","templateKey":"{{{arrangement.TemplateKey}}}","ttlSeconds":{{{Ttl}}},"variables":{"code":"{{{VariableCode}}}"}}
            """;
    }

    private static string DigestOf(string canonical)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeInJsonText();

    private static Dictionary<string, object?> Body(
        AttachmentArrangement arrangement,
        string recipientId,
        string[] references)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["application"] = arrangement.Application,
            ["recipientId"] = recipientId,
            ["class"] = AcceptedAttachmentFlow.NotificationClass,
            ["templateKey"] = arrangement.TemplateKey,
            ["locale"] = "pt-BR",
            ["variables"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = VariableCode,
            },
            ["ttlSeconds"] = Ttl,
        };
        if (references.Length > 0)
        {
            body["attachments"] = references;
        }

        return body;
    }

    private static async Task<Guid> IdOfAsync(HttpResponseMessage response)
    {
        JsonElement answer = await NotificationsApi.ReadJsonAsync(response);
        NotificationId.TryParse(answer.GetProperty("notificationId").GetString(), out Guid id)
            .ShouldBeTrue();
        return id;
    }

    /// <summary>
    /// How many stored documents in the whole table name one reference.
    /// <para>
    /// Asked of the text of the column and of every row, because the claim is
    /// about the absence of a document anywhere and not about the row the test
    /// happens to know the identifier of. <c>position</c> rather than a
    /// pattern match: a reference carries underscores, and an underscore in a
    /// pattern would match any character at all.
    /// </para>
    /// </summary>
    private async Task<int> DocumentsNamingAsync(string reference)
        => await fixture.QueryNotificationsDbAsync(db => db.Database
            .SqlQuery<int>($"""
                SELECT count(*)::int AS "Value"
                FROM notifications.notification
                WHERE accepted_attachments IS NOT NULL
                  AND position({reference} in accepted_attachments::text) > 0
                """)
            .SingleAsync());

    private async Task<Guid> NotificationIdOfAsync(string recipientId)
        => await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .Where(candidate => candidate.RecipientId == recipientId)
            .Select(candidate => candidate.Id)
            .SingleAsync());

    private async Task<int> NotificationCountOfAsync(string recipientId)
        => await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .CountAsync(candidate => candidate.RecipientId == recipientId));

    private async Task<string> StoredHashAsync(string application, string idempotencyKey)
        => await fixture.QueryNotificationsDbAsync(db => db.IdempotencyRegistrations
            .AsNoTracking()
            .Where(candidate => candidate.Application == application
                && candidate.IdempotencyKey == idempotencyKey)
            .Select(candidate => candidate.PayloadHash)
            .SingleAsync());
}
