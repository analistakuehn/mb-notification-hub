using System.Globalization;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.AttachmentManagement;

public sealed class AttachmentLifecycleTests
{
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
    [InlineData("billing-app", "invoice.pdf", "application/pdf", 30_000_001)]
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
            DateTimeOffset.UtcNow);

        result.IsFailure.ShouldBeTrue();
        result.Value.ShouldBeNull();
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
                DateTimeOffset.UtcNow)
            .Value
            .ShouldNotBeNull();
}
