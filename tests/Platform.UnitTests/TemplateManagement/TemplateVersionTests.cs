using System.Globalization;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class TemplateVersionTests
{
    private static readonly TemplateKey Key = TemplateKey.Create("auth.otp.login").Value!;
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Channel Email = Channel.Create("email").Value!;
    private static readonly Channel Sms = Channel.Create("sms").Value!;
    private static readonly Locale PtBr = Locale.Create("pt-BR").Value!;

    /// <summary>
    /// A source the ceiling refuses, chosen at 200000 characters and not at one
    /// past the ceiling on purpose: it sits well under the 512000 the aggregate
    /// carried before the ceiling became one number, so the assertion cannot be
    /// satisfied by that older limit.
    /// </summary>
    private static readonly string OverTheCeiling = new('a', 200_000);

    [Fact]
    public void A_new_draft_is_empty_and_records_its_author()
    {
        var draft = TemplateVersion.CreateDraft(Key, 1, "author-1", CreatedAt);

        draft.Status.ShouldBe(TemplateVersionStatus.Draft);
        draft.Version.ShouldBe(1);
        draft.CreatedBy.ShouldBe("author-1");
        draft.CreatedAt.ShouldBe(CreatedAt);
        draft.Contents.ShouldBeEmpty();
        draft.Editors.ShouldBeEmpty();
        draft.VariablesSchemaJson.ShouldBeNull();
        draft.ContentHash.ShouldMatch("^[0-9a-f]{64}$");
        draft.EntityTag.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Editing_content_stores_it_and_registers_the_editor_once()
    {
        var draft = TemplateVersion.CreateDraft(Key, 1, "author-1", CreatedAt);

        draft.SetContent(new ContentEdit(Email, PtBr, "Seu código", "<p>{{code}}</p>", "{{code}}"), "editor-1")
            .IsSuccess.ShouldBeTrue();
        draft.SetContent(new ContentEdit(Email, PtBr, "Seu código", "<p>{{code}}!</p>", "{{code}}"), "editor-1")
            .IsSuccess.ShouldBeTrue();

        draft.Contents.Count.ShouldBe(1);
        draft.Contents[0].Body.ShouldBe("<p>{{code}}!</p>");
        draft.Editors.ShouldBe(["editor-1"]);
    }

    [Fact]
    public void Every_edit_rotates_the_entity_tag_and_refreshes_the_content_hash()
    {
        var draft = TemplateVersion.CreateDraft(Key, 1, "author-1", CreatedAt);
        var initialTag = draft.EntityTag;
        var initialHash = draft.ContentHash;

        draft.SetContent(new ContentEdit(Sms, PtBr, null, "Código: {{code}}", null), "editor-1")
            .IsSuccess.ShouldBeTrue();

        draft.EntityTag.ShouldNotBe(initialTag);
        draft.ContentHash.ShouldNotBe(initialHash);
    }

    [Fact]
    public void The_content_hash_does_not_depend_on_edit_order()
    {
        var first = TemplateVersion.CreateDraft(Key, 1, "author-1", CreatedAt);
        var second = TemplateVersion.CreateDraft(Key, 1, "author-2", CreatedAt);

        first.SetContent(new ContentEdit(Email, PtBr, "Assunto", "corpo-email", "texto"), "editor-1");
        first.SetContent(new ContentEdit(Sms, PtBr, null, "corpo-sms", null), "editor-1");
        second.SetContent(new ContentEdit(Sms, PtBr, null, "corpo-sms", null), "editor-2");
        second.SetContent(new ContentEdit(Email, PtBr, "Assunto", "corpo-email", "texto"), "editor-2");

        first.ContentHash.ShouldBe(second.ContentHash);
    }

    [Fact]
    public void The_content_hash_distinguishes_an_absent_subject_from_an_empty_one()
    {
        var withNull = TemplateVersion.CreateDraft(Key, 1, "author-1", CreatedAt);
        var withEmpty = TemplateVersion.CreateDraft(Key, 1, "author-1", CreatedAt);

        withNull.SetContent(new ContentEdit(Sms, PtBr, null, "corpo", null), "editor-1");
        withEmpty.SetContent(new ContentEdit(Sms, PtBr, string.Empty, "corpo", null), "editor-1");

        withNull.ContentHash.ShouldNotBe(withEmpty.ContentHash);
    }

    [Fact]
    public void Replacing_the_variables_schema_refreshes_the_content_hash()
    {
        var draft = TemplateVersion.CreateDraft(Key, 1, "author-1", CreatedAt);
        var initialHash = draft.ContentHash;

        Result result = draft.SetVariablesSchema("""{"type":"object"}""", "editor-2");

        result.IsSuccess.ShouldBeTrue();
        draft.VariablesSchemaJson.ShouldBe("""{"type":"object"}""");
        draft.ContentHash.ShouldNotBe(initialHash);
        draft.Editors.ShouldBe(["editor-2"]);
    }

    [Fact]
    public void The_content_hash_ignores_schema_whitespace_and_key_order()
    {
        var first = TemplateVersion.CreateDraft(Key, 1, "author-1", CreatedAt);
        var second = TemplateVersion.CreateDraft(Key, 1, "author-1", CreatedAt);

        first.SetVariablesSchema("""{"type":"object","required":["code"]}""", "editor-1");
        second.SetVariablesSchema("""{ "required": ["code"], "type": "object" }""", "editor-1");

        first.ContentHash.ShouldBe(second.ContentHash);
    }

    [Fact]
    public void Cloning_copies_schema_and_contents_and_preserves_the_content_hash()
    {
        var source = TemplateVersion.CreateDraft(Key, 1, "author-1", CreatedAt);
        source.SetContent(new ContentEdit(Email, PtBr, "Assunto", "corpo", "texto"), "author-1");
        source.SetVariablesSchema("""{"type":"object"}""", "author-1");

        Result<TemplateVersion> cloned = TemplateVersion.CreateDraftFrom(
            source, 2, "author-2", CreatedAt.AddDays(1));

        cloned.IsSuccess.ShouldBeTrue();
        TemplateVersion clone = cloned.Value!;
        clone.Version.ShouldBe(2);
        clone.Status.ShouldBe(TemplateVersionStatus.Draft);
        clone.CreatedBy.ShouldBe("author-2");
        clone.Editors.ShouldBeEmpty();
        clone.Contents.Count.ShouldBe(1);
        clone.Contents[0].Subject.ShouldBe("Assunto");
        clone.VariablesSchemaJson.ShouldBe("""{"type":"object"}""");
        clone.ContentHash.ShouldBe(source.ContentHash);
        clone.EntityTag.ShouldNotBe(source.EntityTag);
    }

    [Fact]
    public void A_published_version_rejects_content_edits_naming_the_allowed_transitions()
    {
        TemplateVersion published = RehydratePublished();

        Result result = published.SetContent(new ContentEdit(Email, PtBr, "Assunto", "corpo", null), "editor-1");

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.BusinessRule);
        DomainErrorInfo info = DomainError.Describe(result.Error, result.ErrorKind);
        info.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        info.CurrentStatus.ShouldBe("published");
        info.AllowedTransitions.ShouldBe(["superseded"]);
    }

    [Fact]
    public void A_published_version_rejects_schema_edits()
    {
        TemplateVersion published = RehydratePublished();

        Result result = published.SetVariablesSchema("""{"type":"object"}""", "editor-1");

        result.IsFailure.ShouldBeTrue();
        DomainError.Describe(result.Error, result.ErrorKind).Code.ShouldBe(ErrorCodes.InvalidStateTransition);
    }

    [Fact]
    public void Rejects_blank_content_bodies()
    {
        var draft = TemplateVersion.CreateDraft(Key, 1, "author-1", CreatedAt);

        Result result = draft.SetContent(new ContentEdit(Sms, PtBr, null, "   ", null), "editor-1");

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Validation);
        draft.Contents.ShouldBeEmpty();
    }

    [Fact]
    public void Content_longer_than_the_source_ceiling_is_refused()
    {
        var draft = TemplateVersion.CreateDraft(Key, 1, "author-1", CreatedAt);

        Result result = draft.SetContent(
            new ContentEdit(Email, PtBr, "Assunto", OverTheCeiling, null),
            "editor-1");

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Validation);
        DomainError.Describe(result.Error, result.ErrorKind).Detail
            .ShouldContain(TemplateSourceSize.MaxChars.ToString(CultureInfo.InvariantCulture));
        draft.Contents.ShouldBeEmpty();
    }

    [Fact]
    public void Text_content_longer_than_the_source_ceiling_is_refused()
    {
        var draft = TemplateVersion.CreateDraft(Key, 1, "author-1", CreatedAt);

        Result result = draft.SetContent(
            new ContentEdit(Email, PtBr, "Assunto", "<p>corpo</p>", OverTheCeiling),
            "editor-1");

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Validation);
        DomainError.Describe(result.Error, result.ErrorKind).Detail
            .ShouldContain(TemplateSourceSize.MaxChars.ToString(CultureInfo.InvariantCulture));
        draft.Contents.ShouldBeEmpty();
    }

    private static TemplateVersion RehydratePublished()
        => TemplateVersion.Rehydrate(new TemplateVersionState
        {
            TemplateKey = Key.Value,
            Version = 1,
            Status = "published",
            CreatedBy = "author-1",
            CreatedAt = CreatedAt,
            Contents = [new TemplateContentState("email", "pt-BR", "Assunto", "corpo", null)],
        });
}
