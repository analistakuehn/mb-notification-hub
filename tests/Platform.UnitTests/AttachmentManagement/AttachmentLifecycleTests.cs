using System.Globalization;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.AttachmentManagement;

public sealed class AttachmentLifecycleTests
{
    /// <summary>
    /// A ceiling these oracles pick for themselves. The aggregate is handed one
    /// instead of holding one, so what is provable here is the rule and never
    /// the number: restating the approved capacity would tie every reading
    /// below to a value none of them measures, and would go red the day product
    /// moved it for a reason that has nothing to do with this file.
    /// </summary>
    private const long SizeCeiling = 1_024;

    [Fact]
    public void Public_reference_has_a_stable_prefix_and_round_trips_as_an_opaque_value()
    {
        AttachmentReference reference = AttachmentReference.Generate();

        reference.Value.ShouldStartWith("att_");
        reference.Value.Length.ShouldBe(36);
        AttachmentReference.Create(reference.Value).Value.ShouldBe(reference);
        AttachmentReference.Create(reference.Value.ToUpperInvariant()).IsFailure.ShouldBeTrue();
        AttachmentReference.Create(reference.Value + "0").IsFailure.ShouldBeTrue();
        AttachmentReference.Create("attachment/owned-object").IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Registration_keeps_the_public_reference_separate_from_the_content_identity()
    {
        DateTimeOffset now = DateTimeOffset.Parse(
            "2026-08-31T12:00:00Z",
            CultureInfo.InvariantCulture);

        Result<Attachment> result = Attachment.Register(
            "billing-app",
            "invoice.pdf",
            "application/pdf",
            128,
            SizeCeiling,
            now);

        result.IsSuccess.ShouldBeTrue();
        Attachment attachment = result.Value.ShouldNotBeNull();
        attachment.Reference.Value.ShouldNotContain(attachment.ContentId.ToString("N"));
        attachment.ContentId.ShouldNotBe(Guid.Empty);
        attachment.State.ShouldBe(AttachmentStates.AwaitingUpload);
        attachment.CreatedAt.ShouldBe(now);
        attachment.ReceivedAt.ShouldBeNull();
    }

    [Theory]
    [InlineData("", "invoice.pdf", "application/pdf", 10)]
    [InlineData("billing-app", "", "application/pdf", 10)]
    [InlineData("billing-app", "invoice.pdf", "", 10)]
    [InlineData("billing-app", "invoice.pdf", "not a media type", 10)]
    [InlineData("billing-app", "invoice.pdf", "application/pdf", 0)]
    [InlineData("billing-app", "invoice.pdf", "application/pdf", -1)]
    [InlineData("billing-app", "invoice.pdf", "application/pdf", SizeCeiling + 1)]
    public void Invalid_registration_metadata_is_rejected_without_an_attachment(
        string application,
        string fileName,
        string contentType,
        long sizeBytes)
    {
        Result<Attachment> result = Attachment.Register(
            application,
            fileName,
            contentType,
            sizeBytes,
            SizeCeiling,
            DateTimeOffset.UtcNow);

        result.IsFailure.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }

    /// <summary>
    /// The ceiling admits itself and refuses the next byte. Without the pair,
    /// an off by one in either direction reads as green: a rule that refused
    /// the ceiling itself would still refuse everything above it, and one that
    /// admitted a byte past it would still refuse everything far above.
    /// </summary>
    [Fact]
    public void The_ceiling_it_is_handed_is_the_last_size_a_registration_accepts()
    {
        Attachment.Register(
                "billing-app",
                "invoice.pdf",
                "application/pdf",
                SizeCeiling,
                SizeCeiling,
                DateTimeOffset.UtcNow)
            .IsSuccess.ShouldBeTrue();
        Attachment.Register(
                "billing-app",
                "invoice.pdf",
                "application/pdf",
                SizeCeiling + 1,
                SizeCeiling,
                DateTimeOffset.UtcNow)
            .IsFailure.ShouldBeTrue();
    }

    /// <summary>
    /// The ceiling is the argument and not a number the aggregate keeps, so the
    /// same size is accepted or refused by what the caller hands over. This is
    /// the reading that goes red if a constant ever comes back into the
    /// aggregate, whatever that constant were set to.
    /// </summary>
    [Fact]
    public void The_same_size_is_decided_by_the_ceiling_the_caller_hands_over()
    {
        const long size = SizeCeiling + 1;

        Attachment.Register(
                "billing-app",
                "invoice.pdf",
                "application/pdf",
                size,
                SizeCeiling,
                DateTimeOffset.UtcNow)
            .IsFailure.ShouldBeTrue();
        Attachment.Register(
                "billing-app",
                "invoice.pdf",
                "application/pdf",
                size,
                size,
                DateTimeOffset.UtcNow)
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Matching_content_advances_once_to_received()
    {
        Attachment attachment = RegisteredAttachment(sizeBytes: 4);
        DateTimeOffset receivedAt = DateTimeOffset.Parse(
            "2026-08-31T12:01:00Z",
            CultureInfo.InvariantCulture);

        AttachmentReceiveOutcome first = attachment.MarkReceived(4, receivedAt);
        AttachmentReceiveOutcome repeated = attachment.MarkReceived(4, receivedAt.AddMinutes(1));

        first.ShouldBe(AttachmentReceiveOutcome.Received);
        repeated.ShouldBe(AttachmentReceiveOutcome.AlreadyReceived);
        attachment.State.ShouldBe(AttachmentStates.Received);
        attachment.ReceivedAt.ShouldBe(receivedAt);
    }

    [Fact]
    public void Divergent_content_length_does_not_advance_the_state()
    {
        Attachment attachment = RegisteredAttachment(sizeBytes: 4);

        AttachmentReceiveOutcome outcome = attachment.MarkReceived(
            3,
            DateTimeOffset.UtcNow);

        outcome.ShouldBe(AttachmentReceiveOutcome.SizeMismatch);
        attachment.State.ShouldBe(AttachmentStates.AwaitingUpload);
        attachment.ReceivedAt.ShouldBeNull();
    }

    private static Attachment RegisteredAttachment(long sizeBytes)
        => Attachment.Register(
                "billing-app",
                "invoice.pdf",
                "application/pdf",
                sizeBytes,
                SizeCeiling,
                DateTimeOffset.UtcNow)
            .Value
            .ShouldNotBeNull();
}
