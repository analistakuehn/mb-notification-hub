using System.Text.Json;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// A document can parse and still not transcode, and the refusal for that was
/// written for the producer payload and never for the document the author
/// writes. These tests drive the authoring side of the same rule: the schema of
/// a template version and the definition of a class policy.
/// </summary>
public sealed class UnreadableJsonRefusalTests
{
    private static readonly TemplateKey Key = TemplateKey.Create("auth.otp.login").Value!;
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Channel Email = Channel.Create("email").Value!;
    private static readonly Locale PtBr = Locale.Create("pt-BR").Value!;

    /// <summary>
    /// Raw text on purpose. Spelled as a C# escape the compiler would fold it
    /// into one code unit and the document under test would never carry the six
    /// characters that make it legal JSON text nobody can transcode.
    /// </summary>
    private const string LoneSurrogateEscape = @"\ud800";

    /// <summary>
    /// The escape sits where nothing in the declaration walk reads it. The
    /// literals are concatenated rather than interpolated because the closing
    /// brace runs of a JSON Schema collide with the interpolation delimiters.
    /// </summary>
    private const string SurrogateInValue =
        "{\"properties\":{\"a\":{\"type\":\"string\",\"title\":\"" + LoneSurrogateEscape + "\"}}}";

    /// <summary>The escape sits in a property name, which the declaration walk does read.</summary>
    private const string SurrogateInName =
        "{\"properties\":{\"" + LoneSurrogateEscape + "\":{\"type\":\"string\"}}}";

    private const string PoisonedDefinition =
        "{\"schemaVersion\":1,\"consentPurpose\":\"" + LoneSurrogateEscape + "\"}";

    [Fact]
    public void The_escapes_under_test_are_legal_json_text_that_reaches_the_aggregate()
    {
        // The premise, asserted rather than assumed. A document that never
        // parsed would be refused for being malformed and every refusal below
        // would prove nothing about the rule it claims to test.
        SurrogateInValue.Contains(LoneSurrogateEscape, StringComparison.Ordinal)
            .ShouldBeTrue("O schema deve carregar o escape cru, e não um caractere dobrado pelo compilador.");
        SurrogateInName.Contains(LoneSurrogateEscape, StringComparison.Ordinal)
            .ShouldBeTrue("O schema deve carregar o escape cru, e não um caractere dobrado pelo compilador.");
        PoisonedDefinition.Contains(LoneSurrogateEscape, StringComparison.Ordinal)
            .ShouldBeTrue("A definição deve carregar o escape cru, e não um caractere dobrado pelo compilador.");
        Should.NotThrow(() => JsonDocument.Parse(SurrogateInValue).Dispose());
        Should.NotThrow(() => JsonDocument.Parse(SurrogateInName).Dispose());
        Should.NotThrow(() => JsonDocument.Parse(PoisonedDefinition).Dispose());
    }

    [Fact]
    public void A_schema_that_does_not_transcode_is_refused_at_the_door_and_never_stored()
    {
        var draft = TemplateVersion.CreateDraft(Key, 1, "author-1", CreatedAt);
        var hashBefore = draft.ContentHash;

        Result result = Should.NotThrow(() => draft.SetVariablesSchema(SurrogateInValue, "editor-1"));

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Validation);
        DomainError.Describe(result.Error, result.ErrorKind).Code
            .ShouldBe(ErrorCodes.VariablesSchemaUnreadable);
        draft.VariablesSchemaJson.ShouldBeNull();
        draft.ContentHash.ShouldBe(hashBefore);
    }

    [Fact]
    public void A_schema_that_is_legal_json_and_not_an_object_is_refused_by_the_aggregate()
    {
        // Left out of the domain, this one publishes: the declaration walk
        // reads no properties out of it, the catalog reports the schema
        // readable, and every undeclared-name check passes over a version that
        // declares nothing at all.
        var draft = TemplateVersion.CreateDraft(Key, 1, "author-1", CreatedAt);

        Result result = draft.SetVariablesSchema("\"texto\"", "editor-1");

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Validation);
        draft.VariablesSchemaJson.ShouldBeNull();
    }

    [Fact]
    public void Editing_the_content_of_a_draft_whose_stored_schema_stopped_reading_is_refused()
    {
        TemplateVersion draft = DraftWithStoredSchema(SurrogateInValue);

        Result result = Should.NotThrow(() => draft.SetContent(
            new ContentEdit(Email, PtBr, "Assunto", "<p>corpo</p>", "corpo"),
            "editor-1"));

        result.IsFailure.ShouldBeTrue();
        DomainError.Describe(result.Error, result.ErrorKind).Code
            .ShouldBe(ErrorCodes.StoredContentUnreadable);

        // The refusal leaves the aggregate where it found it: a version that
        // took the edit and then failed to rehash would carry a hash that
        // vouches for content it no longer holds.
        draft.Contents.ShouldBeEmpty();
    }

    [Fact]
    public void Pinning_a_layout_on_a_draft_whose_stored_schema_stopped_reading_is_refused()
    {
        TemplateVersion draft = DraftWithStoredSchema(SurrogateInValue);

        Result result = Should.NotThrow(() => draft.SetLayoutReference(
            LayoutKey.Create("corp.base").Value!, 3, "editor-1"));

        result.IsFailure.ShouldBeTrue();
        DomainError.Describe(result.Error, result.ErrorKind).Code
            .ShouldBe(ErrorCodes.StoredContentUnreadable);
        draft.LayoutKey.ShouldBeNull();
    }

    [Fact]
    public void Verifying_a_version_whose_stored_schema_stopped_reading_names_that_and_not_a_mismatch()
    {
        TemplateVersion version = DraftWithStoredSchema(SurrogateInValue);

        Result result = Should.NotThrow(version.VerifyContentHash);

        result.IsFailure.ShouldBeTrue();
        DomainErrorInfo info = DomainError.Describe(result.Error, result.ErrorKind);

        // The content did not diverge from its hash: it cannot be read at all,
        // and reporting a mismatch would accuse the stored bytes of a change
        // nobody made.
        info.Code.ShouldBe(ErrorCodes.StoredContentUnreadable);
        info.Code.ShouldNotBe(ErrorCodes.ContentHashMismatch);
    }

    [Fact]
    public void Reading_the_declarations_of_a_schema_that_does_not_transcode_answers_false()
    {
        // The method promises in its name that it does not throw, and the
        // publication catalog is built on that promise: a schema that takes it
        // down takes the whole gate down with it.
        var parsed = Should.NotThrow(() => VariablesSchema.TryParse(
            SurrogateInName, out IReadOnlyList<VariableDeclaration> _));

        parsed.ShouldBeFalse();
    }

    [Fact]
    public void Resolving_names_against_a_schema_that_does_not_transcode_answers_false()
    {
        string[] names = ["cpf"];

        var resolved = Should.NotThrow(() => VariablesSchema.TryUndeclaredNames(
            SurrogateInName, names, out IReadOnlyList<string> _));

        resolved.ShouldBeFalse();
    }

    [Fact]
    public void A_policy_definition_that_does_not_transcode_is_refused_as_a_validation_failure()
    {
        Result<ClassPolicyVersion> draft = Should.NotThrow(() => ClassPolicyVersion.CreateDraft(
            new ClassPolicyDraftInput
            {
                Application = "billing",
                Class = NotificationClasses.Create("transactional").Value,
                Version = 1,
                DefinitionJson = PoisonedDefinition,
                CreatedBy = "author-1",
                CreatedAt = CreatedAt,
            }));

        draft.IsFailure.ShouldBeTrue();
        draft.ErrorKind.ShouldBe(ResultErrorKind.Validation);
    }

    [Fact]
    public void Verifying_a_policy_whose_stored_definition_stopped_reading_is_refused()
    {
        ClassPolicyVersion version = ClassPolicyVersion.Rehydrate(new ClassPolicyVersionState
        {
            Application = "billing",
            Class = NotificationClasses.Create("transactional").Value,
            Version = 1,
            Status = "draft",
            SchemaVersion = 1,
            DefinitionJson = PoisonedDefinition,
            CreatedBy = "author-1",
            CreatedAt = CreatedAt,
            ContentHash = new string('0', 64),
        });

        Result result = Should.NotThrow(version.VerifyContentHash);

        result.IsFailure.ShouldBeTrue();
        DomainError.Describe(result.Error, result.ErrorKind).Code
            .ShouldBe(ErrorCodes.StoredContentUnreadable);
    }

    /// <summary>
    /// A draft holding a schema no door would accept today. The stored hash is
    /// supplied so rehydration does not have to derive one from a document it
    /// cannot read, which is the state a row written before the door existed
    /// is in.
    /// </summary>
    private static TemplateVersion DraftWithStoredSchema(string schemaJson)
        => TemplateVersion.Rehydrate(new TemplateVersionState
        {
            TemplateKey = Key.Value,
            Version = 1,
            Status = "draft",
            CreatedBy = "author-1",
            CreatedAt = CreatedAt,
            VariablesSchemaJson = schemaJson,
            ContentHash = new string('0', 64),
        });
}
