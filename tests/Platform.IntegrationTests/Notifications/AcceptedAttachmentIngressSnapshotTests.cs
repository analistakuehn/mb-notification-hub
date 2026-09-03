using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.IntegrationTests.AttachmentManagement;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications;

/// <summary>
/// What the acceptance leaves on the notification row when the request named
/// attachments.
/// <para>
/// The claim already proves that the set was held; this proves that what was
/// held was written down. The two are different failures: a notification
/// accepted over a set nobody wrote down is one whose every later attempt has
/// to ask the owning module what the composition was, and the answer it would
/// get is the composition as it stands then, not the one that was accepted.
/// </para>
/// <para>
/// The oracle holds the stored document against what the arrangement seeded,
/// never against what the document itself says. A snapshot compared to itself
/// would pass over a writer that stored the wrong set.
/// </para>
/// </summary>
[Collection(NotificationsApiCollectionDefinition.Name)]
public sealed class AcceptedAttachmentIngressSnapshotTests(NotificationsApiFixture fixture)
{
    private const string Route = "/v1/notifications";

    /// <summary>
    /// The set the acceptance claimed is the set the row carries, in the order
    /// the request declared it. The order is reversed against the order the
    /// attachments were seeded in, so a writer that stored the set in any
    /// order of its own fails here.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_acceptance_writes_down_the_set_it_claimed_in_the_order_it_was_asked_for()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        HttpClient producer = fixture.CreateProducerClient(
            "producer-snapshot-written", NotificationsApi.SendTransactional);
        SeededAttachment first = await ClaimableAttachments.ReleasedAsync(
            fixture, NotificationsApi.Application, "contrato.pdf", "application/pdf", 4096);
        SeededAttachment second = await ClaimableAttachments.ReleasedAsync(
            fixture, NotificationsApi.Application, "anexo-b.txt", "text/plain", 17);
        var idempotencyKey = $"snapshot-written-{Guid.NewGuid():N}";

        HttpResponseMessage accepted = await PostAsync(
            producer,
            Body(templateKey, [second.Reference, first.Reference]),
            idempotencyKey);

        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        AcceptedAttachmentSet stored = await StoredSetAsync(idempotencyKey);

        stored.Select(item => item.Reference).ShouldBe([second.Reference, first.Reference]);
        stored[0].ContentIdentity.ShouldBe(second.ContentIdentity);
        stored[0].Name.ShouldBe(second.Name);
        stored[0].MediaType.ShouldBe(second.MediaType);
        stored[0].Length.ShouldBe(second.Length);
        stored[1].ContentIdentity.ShouldBe(first.ContentIdentity);
        stored[1].Name.ShouldBe(first.Name);
        stored[1].MediaType.ShouldBe(first.MediaType);
        stored[1].Length.ShouldBe(first.Length);
    }

    /// <summary>
    /// A request that named no attachments leaves the column empty, and empty
    /// is not the same answer as a document nobody can read. Without this, a
    /// writer that stored an empty envelope would make every notification in
    /// the service carry a set, and the ordinary path would become the one
    /// that has to be told apart from a defect.
    /// </summary>
    [RequiresDockerFact]
    public async Task An_acceptance_that_named_no_attachments_leaves_the_column_empty()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        HttpClient producer = fixture.CreateProducerClient(
            "producer-snapshot-absent", NotificationsApi.SendTransactional);
        var idempotencyKey = $"snapshot-absent-{Guid.NewGuid():N}";

        HttpResponseMessage accepted = await PostAsync(
            producer, Body(templateKey, attachments: null), idempotencyKey);

        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        AcceptedAttachmentManifest.Read(await StoredDocumentAsync(idempotencyKey))
            .ShouldBeOfType<AcceptedManifestRead.Absent>();
    }

    private async Task<AcceptedAttachmentSet> StoredSetAsync(string idempotencyKey)
        => AcceptedAttachmentManifest.Read(await StoredDocumentAsync(idempotencyKey))
            .ShouldBeOfType<AcceptedManifestRead.Present>()
            .Accepted;

    private async Task<string?> StoredDocumentAsync(string idempotencyKey)
        => await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .Where(candidate => candidate.IdempotencyKey == idempotencyKey)
            .Select(candidate => candidate.AcceptedAttachmentsJson)
            .SingleAsync());

    private static Dictionary<string, object?> Body(
        string templateKey,
        IReadOnlyList<string>? attachments)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["application"] = NotificationsApi.Application,
            ["recipientId"] = $"cus_{Guid.NewGuid():N}",
            ["class"] = "transactional",
            ["templateKey"] = templateKey,
            ["variables"] = new { orderId = "ord-1" },
            ["ttlSeconds"] = 300,
        };
        if (attachments is not null) body["attachments"] = attachments;

        return body;
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        object body,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }
}
