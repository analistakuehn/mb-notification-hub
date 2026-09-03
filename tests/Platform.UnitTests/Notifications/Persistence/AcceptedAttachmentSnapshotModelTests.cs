using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

namespace NotificationHub.UnitTests.Notifications.Persistence;

/// <summary>
/// The snapshot is written by the acceptance and by nothing else, and this is
/// where that is stated rather than practised. A habit spread across the paths
/// that touch a notification holds only for as long as every one of them
/// remembers; the entity refuses a second snapshot in memory, and the mapping
/// refuses one on a row that already exists.
/// </summary>
public sealed class AcceptedAttachmentSnapshotModelTests
{
    private static readonly string Document = AcceptedAttachmentManifest.Serialize(
        AcceptedAttachmentSet.Of([Attachment("att_alpha")]));

    private static readonly string OtherDocument = AcceptedAttachmentManifest.Serialize(
        AcceptedAttachmentSet.Of([Attachment("att_omega")]));

    [Fact]
    public void An_acceptance_carries_no_snapshot_until_one_is_frozen()
        => Accepted().AcceptedAttachmentsJson.ShouldBeNull();

    [Fact]
    public void An_acceptance_carries_the_snapshot_it_was_given()
    {
        Notification notification = Accepted();

        notification.FreezeAcceptedAttachments(Document);

        notification.AcceptedAttachmentsJson.ShouldBe(Document);
    }

    /// <summary>
    /// A second snapshot is refused rather than overwritten. A notification
    /// stands for one acceptance over one set, so the only two ways a second
    /// call could happen are an acceptance that claimed twice and something
    /// after the acceptance deciding to change what had been accepted.
    /// </summary>
    [Fact]
    public void A_second_snapshot_is_refused_and_the_first_one_stands()
    {
        Notification notification = Accepted();
        notification.FreezeAcceptedAttachments(Document);

        Should.Throw<InvalidOperationException>(
            () => notification.FreezeAcceptedAttachments(OtherDocument));

        notification.AcceptedAttachmentsJson.ShouldBe(Document);
    }

    /// <summary>
    /// The snapshot belongs to the acceptance, so a notification that has
    /// already moved on cannot receive one. Without this, a path that learned
    /// of a set after the dispatch could attach it to a notification whose
    /// attempts were already built without it.
    /// </summary>
    [Fact]
    public void A_notification_that_already_moved_on_refuses_a_snapshot()
    {
        Notification notification = Accepted();
        notification.MarkDispatched(policyVersion: 3, admittedPlanJson: "[]");

        Should.Throw<InvalidOperationException>(
            () => notification.FreezeAcceptedAttachments(Document));

        notification.AcceptedAttachmentsJson.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void A_blank_snapshot_is_never_frozen(string blank)
    {
        Notification notification = Accepted();

        Should.Throw<ArgumentException>(() => notification.FreezeAcceptedAttachments(blank));

        notification.AcceptedAttachmentsJson.ShouldBeNull();
    }

    /// <summary>
    /// The mapping is what says the column is written once. Nullable and
    /// without a default value, so a notification that named no attachments
    /// stays distinguishable from one whose document cannot be read; read-only
    /// after save, so a tracked change on a row that already exists fails on
    /// the model rather than becoming an update.
    /// </summary>
    [Fact]
    public void The_snapshot_column_is_a_nullable_document_that_refuses_a_later_write()
    {
        IProperty snapshot = Model
            .FindEntityType(typeof(Notification))
            .ShouldNotBeNull()
            .GetProperty(nameof(Notification.AcceptedAttachmentsJson));

        snapshot.GetColumnName().ShouldBe("accepted_attachments");
        snapshot.GetColumnType().ShouldBe("jsonb");
        snapshot.IsNullable.ShouldBeTrue();
        snapshot.GetDefaultValue().ShouldBeNull();
        snapshot.GetAfterSaveBehavior().ShouldBe(PropertySaveBehavior.Throw);
    }

    /// <summary>
    /// One authority for the accepted set, on the notification row. A second
    /// column anywhere in this model would be a second answer to the same
    /// question, free to disagree with the first for any notification whose
    /// path forgot to copy it.
    /// </summary>
    [Fact]
    public void Only_the_notification_row_carries_the_accepted_set()
    {
        var carriers = Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties()
                .Where(CarriesTheAcceptedSet)
                .Select(property => entity.ShortName() + "." + property.Name))
            .Order(StringComparer.Ordinal)
            .ToArray();

        carriers.ShouldBe(["Notification.AcceptedAttachmentsJson"]);

        // A walk that stopped finding entity types would make the assertion
        // above pass over nothing, and every table added later would join it.
        Model.GetEntityTypes().Count().ShouldBeGreaterThan(5);
    }

    private static bool CarriesTheAcceptedSet(IProperty property)
        => property.Name.Contains("AcceptedAttachment", StringComparison.OrdinalIgnoreCase)
            || (property.GetColumnName() is { } column
                && column.Contains("accepted_attachment", StringComparison.OrdinalIgnoreCase));

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

    private static AcceptedAttachment Attachment(string reference) => new()
    {
        Reference = reference,
        ContentIdentity = "content_" + reference,
        Name = reference + ".pdf",
        MediaType = "application/pdf",
        Length = 11,
    };

    // The model is built from the mapping alone and never opens a connection,
    // so the address below only has to be well formed.
    private static IModel Model { get; } = BuildModel();

    private static IModel BuildModel()
    {
        using var context = new NotificationsDbContext(
            new DbContextOptionsBuilder<NotificationsDbContext>()
                .UseNpgsql("Host=127.0.0.1;Database=notifications-model-only")
                .Options);
        return context.Model;
    }
}
