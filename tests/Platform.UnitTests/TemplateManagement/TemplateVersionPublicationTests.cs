using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class TemplateVersionPublicationTests
{
    private static readonly TemplateKey Key = TemplateKey.Create("auth.otp.login").Value!;
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PublishedAt = new(2026, 8, 22, 15, 0, 0, TimeSpan.Zero);
    private static readonly Channel Email = Channel.Create("email").Value!;
    private static readonly Locale PtBr = Locale.Create("pt-BR").Value!;

    [Fact]
    public void A_publisher_who_neither_created_nor_edited_the_draft_publishes_it()
    {
        TemplateVersion draft = DraftWithContent("author-1", "editor-1");
        var previousTag = draft.EntityTag;

        Result result = draft.Publish("publisher-1", PublishedAt);

        result.IsSuccess.ShouldBeTrue();
        draft.Status.ShouldBe(TemplateVersionStatus.Published);
        draft.PublishedAt.ShouldBe(PublishedAt);
        draft.EntityTag.ShouldNotBe(previousTag);
    }

    [Fact]
    public void The_author_cannot_publish_the_version_even_holding_the_role()
    {
        TemplateVersion draft = DraftWithContent("author-1", "editor-1");

        Result result = draft.Publish("author-1", PublishedAt);

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Forbidden);
        DomainError.Describe(result.Error, result.ErrorKind).Code.ShouldBe(ErrorCodes.FourEyesViolation);
        draft.Status.ShouldBe(TemplateVersionStatus.Draft);
        draft.PublishedAt.ShouldBeNull();
    }

    [Fact]
    public void An_editor_cannot_publish_the_version_they_touched()
    {
        TemplateVersion draft = DraftWithContent("author-1", "editor-1");

        Result result = draft.Publish("editor-1", PublishedAt);

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Forbidden);
        DomainError.Describe(result.Error, result.ErrorKind).Code.ShouldBe(ErrorCodes.FourEyesViolation);
        draft.Status.ShouldBe(TemplateVersionStatus.Draft);
    }

    [Fact]
    public void Publishing_an_already_published_version_names_the_allowed_transitions()
    {
        TemplateVersion version = DraftWithContent("author-1", "editor-1");
        version.Publish("publisher-1", PublishedAt).IsSuccess.ShouldBeTrue();

        Result result = version.Publish("publisher-2", PublishedAt.AddMinutes(1));

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.BusinessRule);
        DomainErrorInfo info = DomainError.Describe(result.Error, result.ErrorKind);
        info.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        info.CurrentStatus.ShouldBe("published");
        info.AllowedTransitions.ShouldBe(["superseded"]);
    }

    [Fact]
    public void Superseding_moves_a_published_version_aside()
    {
        TemplateVersion version = DraftWithContent("author-1", "editor-1");
        version.Publish("publisher-1", PublishedAt).IsSuccess.ShouldBeTrue();

        Result result = version.Supersede();

        result.IsSuccess.ShouldBeTrue();
        version.Status.ShouldBe(TemplateVersionStatus.Superseded);
    }

    [Fact]
    public void A_draft_cannot_be_superseded()
    {
        TemplateVersion draft = DraftWithContent("author-1", "editor-1");

        Result result = draft.Supersede();

        result.IsFailure.ShouldBeTrue();
        DomainError.Describe(result.Error, result.ErrorKind).Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        draft.Status.ShouldBe(TemplateVersionStatus.Draft);
    }

    [Fact]
    public void The_content_hash_verification_accepts_untouched_content()
    {
        TemplateVersion draft = DraftWithContent("author-1", "editor-1");

        draft.VerifyContentHash().IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void The_content_hash_verification_rejects_content_that_no_longer_matches_the_stored_hash()
    {
        var tampered = TemplateVersion.Rehydrate(new TemplateVersionState
        {
            TemplateKey = Key.Value,
            Version = 1,
            Status = "draft",
            CreatedBy = "author-1",
            CreatedAt = CreatedAt,
            ContentHash = new string('0', 64),
            Contents = [new TemplateContentState("email", "pt-BR", "Assunto", "corpo", null)],
        });

        Result result = tampered.VerifyContentHash();

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.BusinessRule);
        DomainError.Describe(result.Error, result.ErrorKind).Code.ShouldBe(ErrorCodes.ContentHashMismatch);
    }

    [Fact]
    public void A_rollback_clones_a_superseded_version_and_publishes_it_with_provenance()
    {
        TemplateVersion source = DraftWithContent("author-1", "editor-1");
        source.Publish("publisher-1", PublishedAt).IsSuccess.ShouldBeTrue();
        source.Supersede().IsSuccess.ShouldBeTrue();

        Result<TemplateVersion> result = TemplateVersion.CreateRollback(
            source, 3, "publisher-2", PublishedAt.AddDays(1));

        result.IsSuccess.ShouldBeTrue();
        TemplateVersion published = result.Value!;
        published.Version.ShouldBe(3);
        published.Status.ShouldBe(TemplateVersionStatus.Published);
        published.RolledBackFrom.ShouldBe(source.Version);
        published.PublishedAt.ShouldBe(PublishedAt.AddDays(1));
        published.ContentHash.ShouldBe(source.ContentHash);
        published.CreatedBy.ShouldBe("publisher-2");
    }

    [Fact]
    public void A_version_never_published_cannot_be_a_rollback_target()
    {
        TemplateVersion draft = DraftWithContent("author-1", "editor-1");

        Result<TemplateVersion> result = TemplateVersion.CreateRollback(
            draft, 2, "publisher-1", PublishedAt);

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.BusinessRule);
        DomainErrorInfo info = DomainError.Describe(result.Error, result.ErrorKind);
        info.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        info.CurrentStatus.ShouldBe("draft");
    }

    [Fact]
    public void The_author_of_the_source_version_cannot_roll_back_to_it()
    {
        TemplateVersion source = DraftWithContent("author-1", "editor-1");
        source.Publish("publisher-1", PublishedAt).IsSuccess.ShouldBeTrue();

        Result<TemplateVersion> result = TemplateVersion.CreateRollback(
            source, 2, "author-1", PublishedAt.AddDays(1));

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Forbidden);
        DomainError.Describe(result.Error, result.ErrorKind).Code.ShouldBe(ErrorCodes.FourEyesViolation);
    }

    [Fact]
    public void An_editor_of_the_source_version_cannot_roll_back_to_it()
    {
        TemplateVersion source = DraftWithContent("author-1", "editor-1");
        source.Publish("publisher-1", PublishedAt).IsSuccess.ShouldBeTrue();

        Result<TemplateVersion> result = TemplateVersion.CreateRollback(
            source, 2, "editor-1", PublishedAt.AddDays(1));

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Forbidden);
        DomainError.Describe(result.Error, result.ErrorKind).Code.ShouldBe(ErrorCodes.FourEyesViolation);
    }

    private static TemplateVersion DraftWithContent(string author, string editor)
    {
        var draft = TemplateVersion.CreateDraft(Key, 1, author, CreatedAt);
        draft.SetContent(new ContentEdit(Email, PtBr, "Seu código", "<p>{{code}}</p>", "{{code}}"), editor)
            .IsSuccess.ShouldBeTrue();
        return draft;
    }
}
