using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class LayoutVersionTests
{
    private static readonly LayoutKey Key = LayoutKey.Create("email.base").Value!;
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Channel Email = Channel.Create("email").Value!;
    private static readonly Channel Sms = Channel.Create("sms").Value!;
    private static readonly Locale PtBr = Locale.Create("pt-BR").Value!;

    [Fact]
    public void A_new_draft_is_empty_and_records_its_author()
    {
        var draft = LayoutVersion.CreateDraft(Key, 1, "author-1", CreatedAt);

        draft.Status.ShouldBe(LayoutVersionStatus.Draft);
        draft.Version.ShouldBe(1);
        draft.CreatedBy.ShouldBe("author-1");
        draft.Contents.ShouldBeEmpty();
        draft.Editors.ShouldBeEmpty();
        draft.ContentHash.ShouldMatch("^[0-9a-f]{64}$");
        draft.EntityTag.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Editing_content_stores_it_registers_the_editor_and_rotates_hash_and_tag()
    {
        var draft = LayoutVersion.CreateDraft(Key, 1, "author-1", CreatedAt);
        var initialTag = draft.EntityTag;
        var initialHash = draft.ContentHash;

        draft.SetContent(new LayoutContentEdit(Email, PtBr, "<html>{{ content }}</html>", "{{ content }}"), "editor-1")
            .IsSuccess.ShouldBeTrue();
        draft.SetContent(new LayoutContentEdit(Email, PtBr, "<html>{{ content }}!</html>", "{{ content }}"), "editor-1")
            .IsSuccess.ShouldBeTrue();

        draft.Contents.Count.ShouldBe(1);
        draft.Contents[0].Body.ShouldBe("<html>{{ content }}!</html>");
        draft.Editors.ShouldBe(["editor-1"]);
        draft.EntityTag.ShouldNotBe(initialTag);
        draft.ContentHash.ShouldNotBe(initialHash);
    }

    [Fact]
    public void The_content_hash_does_not_depend_on_edit_order()
    {
        var first = LayoutVersion.CreateDraft(Key, 1, "author-1", CreatedAt);
        var second = LayoutVersion.CreateDraft(Key, 1, "author-2", CreatedAt);

        first.SetContent(new LayoutContentEdit(Email, PtBr, "corpo-email {{ content }}", "texto {{ content }}"), "editor-1");
        first.SetContent(new LayoutContentEdit(Sms, PtBr, "corpo-sms {{ content }}", null), "editor-1");
        second.SetContent(new LayoutContentEdit(Sms, PtBr, "corpo-sms {{ content }}", null), "editor-2");
        second.SetContent(new LayoutContentEdit(Email, PtBr, "corpo-email {{ content }}", "texto {{ content }}"), "editor-2");

        first.ContentHash.ShouldBe(second.ContentHash);
    }

    [Fact]
    public void Cloning_copies_contents_and_preserves_the_content_hash()
    {
        var source = LayoutVersion.CreateDraft(Key, 1, "author-1", CreatedAt);
        source.SetContent(new LayoutContentEdit(Email, PtBr, "<html>{{ content }}</html>", "{{ content }}"), "author-1");

        var clone = LayoutVersion.CreateDraftFrom(source, 2, "author-2", CreatedAt.AddDays(1));

        clone.Version.ShouldBe(2);
        clone.Status.ShouldBe(LayoutVersionStatus.Draft);
        clone.CreatedBy.ShouldBe("author-2");
        clone.Editors.ShouldBeEmpty();
        clone.Contents.Count.ShouldBe(1);
        clone.ContentHash.ShouldBe(source.ContentHash);
        clone.EntityTag.ShouldNotBe(source.EntityTag);
    }

    [Fact]
    public void A_published_version_rejects_content_edits_naming_the_allowed_transitions()
    {
        LayoutVersion published = RehydratePublished("author-1");

        Result result = published.SetContent(new LayoutContentEdit(Email, PtBr, "{{ content }}", null), "editor-1");

        result.IsFailure.ShouldBeTrue();
        DomainErrorInfo info = DomainError.Describe(result.Error, result.ErrorKind);
        info.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        info.CurrentStatus.ShouldBe("published");
        info.AllowedTransitions.ShouldBe(["superseded"]);
    }

    [Fact]
    public void The_author_cannot_publish_their_own_draft()
    {
        var draft = LayoutVersion.CreateDraft(Key, 1, "author-1", CreatedAt);
        draft.SetContent(new LayoutContentEdit(Email, PtBr, "{{ content }}", null), "author-1");

        Result result = draft.Publish("author-1", CreatedAt.AddHours(1));

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Forbidden);
        DomainError.Describe(result.Error, result.ErrorKind).Code.ShouldBe(ErrorCodes.FourEyesViolation);
        draft.Status.ShouldBe(LayoutVersionStatus.Draft);
    }

    [Fact]
    public void An_editor_cannot_publish_the_draft_they_touched()
    {
        var draft = LayoutVersion.CreateDraft(Key, 1, "author-1", CreatedAt);
        draft.SetContent(new LayoutContentEdit(Email, PtBr, "{{ content }}", null), "editor-2");

        Result result = draft.Publish("editor-2", CreatedAt.AddHours(1));

        result.IsFailure.ShouldBeTrue();
        DomainError.Describe(result.Error, result.ErrorKind).Code.ShouldBe(ErrorCodes.FourEyesViolation);
    }

    [Fact]
    public void A_distinct_publisher_publishes_and_the_previous_version_supersedes()
    {
        var draft = LayoutVersion.CreateDraft(Key, 2, "author-1", CreatedAt);
        draft.SetContent(new LayoutContentEdit(Email, PtBr, "{{ content }}", null), "author-1");
        LayoutVersion current = RehydratePublished("author-0");

        draft.Publish("publisher-1", CreatedAt.AddHours(1)).IsSuccess.ShouldBeTrue();
        current.Supersede().IsSuccess.ShouldBeTrue();

        draft.Status.ShouldBe(LayoutVersionStatus.Published);
        draft.PublishedAt.ShouldBe(CreatedAt.AddHours(1));
        current.Status.ShouldBe(LayoutVersionStatus.Superseded);
    }

    [Fact]
    public void Rollback_clones_a_published_version_and_publishes_it_for_a_third_party()
    {
        LayoutVersion source = RehydratePublished("author-1");

        Result<LayoutVersion> rollback = LayoutVersion.CreateRollback(source, 2, "publisher-2", CreatedAt.AddDays(1));

        rollback.IsSuccess.ShouldBeTrue();
        rollback.Value!.Status.ShouldBe(LayoutVersionStatus.Published);
        rollback.Value!.RolledBackFrom.ShouldBe(1);
        rollback.Value!.ContentHash.ShouldBe(source.ContentHash);
    }

    [Fact]
    public void Rollback_by_the_source_author_violates_four_eyes()
    {
        LayoutVersion source = RehydratePublished("author-1");

        Result<LayoutVersion> rollback = LayoutVersion.CreateRollback(source, 2, "author-1", CreatedAt.AddDays(1));

        rollback.IsFailure.ShouldBeTrue();
        rollback.ErrorKind.ShouldBe(ResultErrorKind.Forbidden);
        DomainError.Describe(rollback.Error, rollback.ErrorKind).Code.ShouldBe(ErrorCodes.FourEyesViolation);
    }

    [Fact]
    public void Verifying_the_content_hash_detects_a_stored_value_that_no_longer_matches()
    {
        var tampered = LayoutVersion.Rehydrate(new LayoutVersionState
        {
            LayoutKey = Key.Value,
            Version = 1,
            Status = "published",
            CreatedBy = "author-1",
            CreatedAt = CreatedAt,
            ContentHash = new string('0', 64),
            Contents = [new LayoutContentState("email", "pt-BR", "{{ content }}", null)],
        });

        Result result = tampered.VerifyContentHash();

        result.IsFailure.ShouldBeTrue();
        DomainError.Describe(result.Error, result.ErrorKind).Code.ShouldBe(ErrorCodes.ContentHashMismatch);
    }

    private static LayoutVersion RehydratePublished(string author)
        => LayoutVersion.Rehydrate(new LayoutVersionState
        {
            LayoutKey = Key.Value,
            Version = 1,
            Status = "published",
            CreatedBy = author,
            CreatedAt = CreatedAt,
            Contents = [new LayoutContentState("email", "pt-BR", "<html>{{ content }}</html>", null)],
        });
}
