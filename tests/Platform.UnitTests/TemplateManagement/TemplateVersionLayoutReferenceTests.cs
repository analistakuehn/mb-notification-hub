using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class TemplateVersionLayoutReferenceTests
{
    private static readonly TemplateKey Key = TemplateKey.Create("auth.otp.login").Value!;
    private static readonly LayoutKey BaseLayout = LayoutKey.Create("email.base").Value!;
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Pinning_a_layout_stores_both_fields_and_refreshes_hash_and_tag()
    {
        var draft = TemplateVersion.CreateDraft(Key, 1, "author-1", CreatedAt);
        var initialHash = draft.ContentHash;
        var initialTag = draft.EntityTag;

        Result result = draft.SetLayoutReference(BaseLayout, 3, "editor-1");

        result.IsSuccess.ShouldBeTrue();
        draft.LayoutKey.ShouldBe("email.base");
        draft.LayoutVersion.ShouldBe(3);
        draft.ContentHash.ShouldNotBe(initialHash);
        draft.EntityTag.ShouldNotBe(initialTag);
        draft.Editors.ShouldBe(["editor-1"]);
    }

    [Fact]
    public void Clearing_the_reference_restores_the_hash_of_a_version_without_layout()
    {
        var draft = TemplateVersion.CreateDraft(Key, 1, "author-1", CreatedAt);
        var hashWithoutLayout = draft.ContentHash;
        draft.SetLayoutReference(BaseLayout, 3, "editor-1").IsSuccess.ShouldBeTrue();

        Result result = draft.SetLayoutReference(null, null, "editor-1");

        result.IsSuccess.ShouldBeTrue();
        draft.LayoutKey.ShouldBeNull();
        draft.LayoutVersion.ShouldBeNull();
        draft.ContentHash.ShouldBe(hashWithoutLayout);
    }

    [Fact]
    public void Pinning_a_different_layout_version_changes_the_content_hash()
    {
        var first = TemplateVersion.CreateDraft(Key, 1, "author-1", CreatedAt);
        var second = TemplateVersion.CreateDraft(Key, 1, "author-1", CreatedAt);

        first.SetLayoutReference(BaseLayout, 3, "editor-1");
        second.SetLayoutReference(BaseLayout, 4, "editor-1");

        first.ContentHash.ShouldNotBe(second.ContentHash);
    }

    [Fact]
    public void A_partial_reference_is_rejected()
    {
        var draft = TemplateVersion.CreateDraft(Key, 1, "author-1", CreatedAt);

        Result result = draft.SetLayoutReference(BaseLayout, null, "editor-1");

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Validation);
        draft.LayoutKey.ShouldBeNull();
    }

    [Fact]
    public void A_published_version_rejects_layout_reference_edits()
    {
        var published = TemplateVersion.Rehydrate(new TemplateVersionState
        {
            TemplateKey = Key.Value,
            Version = 1,
            Status = "published",
            CreatedBy = "author-1",
            CreatedAt = CreatedAt,
            Contents = [new TemplateContentState("email", "pt-BR", "Assunto", "corpo", null)],
        });

        Result result = published.SetLayoutReference(BaseLayout, 1, "editor-1");

        result.IsFailure.ShouldBeTrue();
        DomainError.Describe(result.Error, result.ErrorKind).Code.ShouldBe(ErrorCodes.InvalidStateTransition);
    }

    [Fact]
    public void Cloning_a_version_carries_its_layout_reference_and_hash()
    {
        var source = TemplateVersion.CreateDraft(Key, 1, "author-1", CreatedAt);
        source.SetLayoutReference(BaseLayout, 2, "author-1");

        var clone = TemplateVersion.CreateDraftFrom(source, 2, "author-2", CreatedAt.AddDays(1));

        clone.LayoutKey.ShouldBe("email.base");
        clone.LayoutVersion.ShouldBe(2);
        clone.ContentHash.ShouldBe(source.ContentHash);
    }
}
