using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.UnitTests.Notifications;

/// <summary>
/// What every path that could still reach a provider does with the accepted
/// set of the notification it is about to act on.
/// <para>
/// Three answers, and the third one is the reason this exists. A set lets the
/// path go on with the composition the acceptance froze. No set lets it go on
/// with none, which is the ordinary history of a notification that named no
/// attachments. A document nobody can read is neither, and the two ways of
/// pretending it is are both deliveries nobody agreed to: read as absence it
/// sends the notification without the attachments it was accepted over, and
/// read as a set it sends a composition nobody can name.
/// </para>
/// </summary>
public sealed class AcceptedAttachmentRefusalTests
{
    private const string PlantedName = "contrato-9f41d0e6.pdf";

    private const string PlantedReference = "att_9f41d0e6";

    [Fact]
    public void A_notification_that_named_no_attachments_is_let_through()
        => Should.NotThrow(() => AcceptedAttachmentManifest.RefuseUnreadable(Accepted()));

    [Fact]
    public void A_notification_whose_document_reads_is_let_through()
    {
        Notification notification = Accepted();
        notification.FreezeAcceptedAttachments(
            AcceptedAttachmentManifest.Serialize(AcceptedAttachmentSet.Of([Attachment()])));

        Should.NotThrow(() => AcceptedAttachmentManifest.RefuseUnreadable(notification));
    }

    /// <summary>
    /// Every shape the store accepts and this reader does not, each stopping
    /// the caller with the word that names the defect. They are documents
    /// PostgreSQL has no opinion about, so nothing between them and a provider
    /// call refuses them except this.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnreadableDocuments))]
    public void A_document_the_reader_cannot_make_sense_of_stops_the_caller(
        string planted,
        string reason)
    {
        Notification notification = Accepted();
        notification.FreezeAcceptedAttachments(planted);

        AcceptedAttachmentsUnreadableException refusal =
            Should.Throw<AcceptedAttachmentsUnreadableException>(
                () => AcceptedAttachmentManifest.RefuseUnreadable(notification));

        refusal.Reason.ShouldBe(reason);
        refusal.NotificationId.ShouldBe(notification.Id);
        AcceptedAttachmentManifest.Refusals.ShouldContain(refusal.Reason);
    }

    public static TheoryData<string, string> UnreadableDocuments()
    {
        var data = new TheoryData<string, string>();
        data.Add("null", AcceptedAttachmentManifest.RefusedMalformedDocument);
        data.Add("[]", AcceptedAttachmentManifest.RefusedMalformedDocument);
        data.Add("{}", AcceptedAttachmentManifest.RefusedMalformedDocument);
        data.Add(Document(2, PlantedReference), AcceptedAttachmentManifest.RefusedUnknownSchemaVersion);
        data.Add(WithExtraMember(), AcceptedAttachmentManifest.RefusedMalformedDocument);
        data.Add("""{"schemaVersion":1,"items":[]}""", AcceptedAttachmentManifest.RefusedUnusableSet);
        data.Add(Twice(), AcceptedAttachmentManifest.RefusedUnusableSet);
        return data;
    }

    /// <summary>
    /// The refusal says which notification stopped and which shape of defect
    /// stopped it, and quotes nothing from the document.
    /// <para>
    /// The identifier is asserted alongside the absences on purpose: an empty
    /// message would satisfy every absence below without proving anything
    /// about what the refusal says. What must not travel is producer data, and
    /// a reference and a file name are exactly that; an operational trail is
    /// the last place they belong, and the document stays on the row for an
    /// investigation entitled to read it.
    /// </para>
    /// </summary>
    [Fact]
    public void The_refusal_names_the_notification_and_quotes_nothing_from_the_document()
    {
        Notification notification = Accepted();
        notification.FreezeAcceptedAttachments(WithExtraMember());

        AcceptedAttachmentsUnreadableException refusal =
            Should.Throw<AcceptedAttachmentsUnreadableException>(
                () => AcceptedAttachmentManifest.RefuseUnreadable(notification));

        refusal.Message.ShouldContain(notification.Id.ToString());
        refusal.Message.ShouldContain(AcceptedAttachmentManifest.RefusedMalformedDocument);
        refusal.Message.Contains(PlantedReference, StringComparison.OrdinalIgnoreCase)
            .ShouldBeFalse("a recusa não pode publicar a referência do anexo.");
        refusal.Message.Contains(PlantedName, StringComparison.OrdinalIgnoreCase)
            .ShouldBeFalse("a recusa não pode publicar o nome do arquivo.");
        refusal.Message.Contains("bucket", StringComparison.OrdinalIgnoreCase)
            .ShouldBeFalse("a recusa não pode publicar o membro que tornou o documento ilegível.");
    }

    private static string Document(int schemaVersion, string reference)
        => $$"""
            {"schemaVersion":{{schemaVersion}},"items":[{"reference":"{{reference}}","contentIdentity":"content_{{reference}}","name":"{{PlantedName}}","mediaType":"application/pdf","length":2048}]}
            """;

    private static string WithExtraMember()
        => Document(1, PlantedReference)
            .Replace("}]}", """}],"storageKey":"bucket/objeto"}""", StringComparison.Ordinal);

    private static string Twice()
        => Document(1, PlantedReference)
            .Replace(
                "}]}",
                $$"""},{"reference":"{{PlantedReference}}","contentIdentity":"outro","name":"{{PlantedName}}","mediaType":"application/pdf","length":1}]}""",
                StringComparison.Ordinal);

    private static AcceptedAttachment Attachment() => new()
    {
        Reference = PlantedReference,
        ContentIdentity = "content_" + PlantedReference,
        Name = PlantedName,
        MediaType = "application/pdf",
        Length = 2048,
    };

    private static Notification Accepted() => Notification.Accept(new NotificationDraft
    {
        Application = "araia-cambio",
        IdempotencyKey = "key-01J5X9",
        RecipientId = "cus_01J5X9",
        Class = NotificationClasses.Transactional,
        TemplateKey = "billing.invoice",
        TemplateVersion = 1,
        VariablesMaskedJson = "{}",
        RequestedBy = "producer",
        TtlSeconds = 3600,
        AcceptedAt = DateTimeOffset.UtcNow,
    });
}
