using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class ClassPolicyVersionTests
{
    private const string Definition = """
        {
          "schemaVersion": 1,
          "channelsAllowed": ["push", "sms"],
          "deliveryPlan": [{ "channel": "push", "timeout": "30s" }, { "channel": "sms" }],
          "defaultTtl": "300s",
          "dedupeWindow": "60s"
        }
        """;

    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_draft_captures_the_definition_its_schema_version_and_the_author()
    {
        ClassPolicyVersion draft = Draft();

        draft.Application.ShouldBe("araia-cambio");
        draft.Class.ShouldBe(NotificationClass.Critical);
        draft.Version.ShouldBe(1);
        draft.Status.ShouldBe(ClassPolicyVersionStatus.Draft);
        draft.SchemaVersion.ShouldBe(1);
        draft.DefinitionJson.ShouldBe(Definition);
        draft.CreatedBy.ShouldBe("author-1");
        draft.Editors.ShouldBeEmpty();
        draft.ContentHash.Length.ShouldBe(64);
        draft.EntityTag.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_content_hash_ignores_formatting_and_key_order()
    {
        const string reordered = "{\"defaultTtl\":\"300s\",\"dedupeWindow\":\"60s\","
            + "\"deliveryPlan\":[{\"channel\":\"push\",\"timeout\":\"30s\"},{\"channel\":\"sms\"}],"
            + "\"channelsAllowed\":[\"push\",\"sms\"],\"schemaVersion\":1}";

        ClassPolicyVersion first = Draft();
        ClassPolicyVersion second = Draft(definitionJson: reordered);

        second.ContentHash.ShouldBe(first.ContentHash);
    }

    [Fact]
    public void A_logically_different_definition_produces_a_different_hash()
    {
        ClassPolicyVersion first = Draft();
        ClassPolicyVersion second = Draft(definitionJson: Definition.Replace("300s", "600s", StringComparison.Ordinal));

        second.ContentHash.ShouldNotBe(first.ContentHash);
    }

    [Fact]
    public void Editing_the_draft_registers_the_editor_once_and_refreshes_hash_and_entity_tag()
    {
        ClassPolicyVersion draft = Draft();
        var originalHash = draft.ContentHash;
        var originalTag = draft.EntityTag;
        var edited = Definition.Replace("60s", "120s", StringComparison.Ordinal);

        draft.SetDefinition(edited, "editor-1").IsSuccess.ShouldBeTrue();
        draft.SetDefinition(edited, "editor-1").IsSuccess.ShouldBeTrue();

        draft.Editors.ShouldBe(["editor-1"]);
        draft.ContentHash.ShouldNotBe(originalHash);
        draft.EntityTag.ShouldNotBe(originalTag);
    }

    [Fact]
    public void A_draft_rejects_a_definition_that_is_not_a_json_object_with_a_schema_version()
    {
        ClassPolicyVersion draft = Draft();

        Result edited = draft.SetDefinition("""{"channelsAllowed":["push"]}""", "editor-1");

        edited.IsFailure.ShouldBeTrue();
        edited.ErrorKind.ShouldBe(ResultErrorKind.Validation);
    }

    [Fact]
    public void The_author_cannot_publish_their_own_draft()
    {
        ClassPolicyVersion draft = Draft();

        Result published = draft.Publish("author-1", CreatedAt.AddHours(1));

        published.IsFailure.ShouldBeTrue();
        published.ErrorKind.ShouldBe(ResultErrorKind.Forbidden);
        draft.Status.ShouldBe(ClassPolicyVersionStatus.Draft);
    }

    [Fact]
    public void An_editor_cannot_publish_the_draft_either()
    {
        ClassPolicyVersion draft = Draft();
        draft.SetDefinition(Definition.Replace("60s", "90s", StringComparison.Ordinal), "editor-1")
            .IsSuccess.ShouldBeTrue();

        Result published = draft.Publish("editor-1", CreatedAt.AddHours(1));

        published.IsFailure.ShouldBeTrue();
        published.ErrorKind.ShouldBe(ResultErrorKind.Forbidden);
    }

    [Fact]
    public void A_second_person_publishes_and_the_version_becomes_immutable()
    {
        ClassPolicyVersion draft = Draft();
        DateTimeOffset publishedAt = CreatedAt.AddHours(1);

        draft.Publish("publisher-1", publishedAt).IsSuccess.ShouldBeTrue();

        draft.Status.ShouldBe(ClassPolicyVersionStatus.Published);
        draft.PublishedAt.ShouldBe(publishedAt);
        Result edited = draft.SetDefinition(Definition, "editor-2");
        edited.IsFailure.ShouldBeTrue();
        edited.ErrorKind.ShouldBe(ResultErrorKind.BusinessRule);
    }

    [Fact]
    public void Only_a_published_version_can_be_superseded()
    {
        ClassPolicyVersion draft = Draft();

        draft.Supersede().IsFailure.ShouldBeTrue();

        draft.Publish("publisher-1", CreatedAt.AddHours(1)).IsSuccess.ShouldBeTrue();
        draft.Supersede().IsSuccess.ShouldBeTrue();
        draft.Status.ShouldBe(ClassPolicyVersionStatus.Superseded);
    }

    [Fact]
    public void Publishing_anything_but_a_draft_reports_the_allowed_transitions()
    {
        ClassPolicyVersion draft = Draft();
        draft.Publish("publisher-1", CreatedAt.AddHours(1)).IsSuccess.ShouldBeTrue();

        Result again = draft.CanBePublishedBy("publisher-2");

        again.IsFailure.ShouldBeTrue();
        DomainErrorInfo info = DomainError.Describe(again.Error, again.ErrorKind);
        info.Code.ShouldBe("invalid-state-transition");
        info.CurrentStatus.ShouldBe("published");
        info.AllowedTransitions.ShouldBe(["superseded"]);
    }

    [Fact]
    public void Integrity_verification_detects_a_definition_that_no_longer_matches_its_hash()
    {
        ClassPolicyVersion intact = Draft();
        var tampered = ClassPolicyVersion.Rehydrate(new ClassPolicyVersionState
        {
            Application = "araia-cambio",
            Class = NotificationClass.Critical,
            Version = 1,
            Status = "draft",
            SchemaVersion = 1,
            DefinitionJson = Definition,
            CreatedBy = "author-1",
            CreatedAt = CreatedAt,
            ContentHash = new string('f', 64),
        });

        intact.VerifyContentHash().IsSuccess.ShouldBeTrue();
        Result verified = tampered.VerifyContentHash();
        verified.IsFailure.ShouldBeTrue();
        verified.Error.ShouldNotBeNull();
        verified.Error.ShouldContain("content-hash-mismatch");
    }

    private static ClassPolicyVersion Draft(string definitionJson = Definition)
    {
        Result<ClassPolicyVersion> draft = ClassPolicyVersion.CreateDraft(new ClassPolicyDraftInput
        {
            Application = "araia-cambio",
            Class = NotificationClass.Critical,
            Version = 1,
            DefinitionJson = definitionJson,
            CreatedBy = "author-1",
            CreatedAt = CreatedAt,
        });
        draft.IsSuccess.ShouldBeTrue();
        return draft.Value!;
    }
}
