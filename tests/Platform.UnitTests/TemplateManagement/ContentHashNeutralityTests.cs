using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// The fence the rest of this work stands on: every hash is a literal, so a
/// change that moves a byte of the canonical form has to say so here instead
/// of passing unnoticed. A hash that shifts turns every stored version into an
/// integrity failure, which would declare tampering in bulk over content
/// nobody touched.
/// <para>
/// The version hashes were re-pinned once, on purpose, when the declaration of
/// which variables carry sensitive data moved onto the version and into the
/// canonical form. That field is written unconditionally, empty included, so
/// no version keeps its previous bytes and none can: an empty declaration and
/// an absent field must not be the same document. The class-policy hashes did
/// not move, because that form never carried the field. The window for this is
/// closed by the first stored row that has to survive a redeploy; from then on
/// a shift here is a defect, not a decision.
/// </para>
/// </summary>
public sealed class ContentHashNeutralityTests
{
    private static readonly TemplateKey Key = TemplateKey.Create("auth.otp.login").Value!;
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Raw text on purpose. Spelled as a C# escape the compiler would fold
    /// these into code units and the schema under test would never carry the
    /// escape a producer actually sends.
    /// </summary>
    private const string PairedSurrogateEscape = @"\ud83d\ude00";

    private const string EscapedATilde = @"\u00e3";

    /// <summary>
    /// A schema that is legal JSON and not an object. The door refuses it, and
    /// a row written before the door existed still has to hash to the pinned
    /// value, or verifying it would report tampering.
    /// </summary>
    internal const string NonObjectSchema = "\"texto\"";

    /// <summary>
    /// Every spelling the canonical form has to answer for: number literals it
    /// must preserve exactly, key order it must sort, escapes it must resolve,
    /// and a schema at the ceiling the aggregate accepts.
    /// </summary>
    internal static readonly (string Name, string Schema)[] Schemas =
    [
        ("exponent", """{"a":1e2}"""),
        ("trailing-zeros", """{"a":1.500}"""),
        ("decimal-zero", """{"a":1.0}"""),
        ("negative-zero", """{"a":-0}"""),
        ("reordered-keys", """{"b":2,"a":1}"""),
        ("sorted-keys", """{"a":1,"b":2}"""),
        ("paired-surrogate-escape", $$"""{"v":"{{PairedSurrogateEscape}}"}"""),
        ("literal-emoji", """{"v":"😀"}"""),
        ("escaped-accent", $$"""{"cidade":"S{{EscapedATilde}}o Paulo"}"""),
        ("literal-accent", """{"cidade":"São Paulo"}"""),
        ("nested", """
            {
              "type": "object",
              "properties": {
                "code": { "type": "string" },
                "link": { "type": "string", "format": "uri" },
                "order": { "type": "object", "properties": { "id": { "type": "string" } } }
              },
              "required": ["code"]
            }
            """),
        ("at-the-ceiling", AtTheCeiling()),
    ];

    /// <summary>
    /// The hash each corpus entry produces. The values are literals and not a
    /// recomputation on purpose: a test that recomputes both sides with the
    /// same code cannot tell that the code changed.
    /// </summary>
    private static readonly Dictionary<string, string> Pinned = new(StringComparer.Ordinal)
    {
        ["exponent"] = "856b77fcd141cf23a95ac425eaec327456bb2ad5d1c3239eeb6267386f911dba",
        ["trailing-zeros"] = "78131c9a88ac24590acf59abd2e5e893de83abd4aa78d32d62d6da5998e6b033",
        ["decimal-zero"] = "f7dd659ede3a21bed7d9c6598e6b3cb96c16427414cff0347b3f3e1a2853d6ad",
        ["negative-zero"] = "49ad555d135f2dbfb5ec8aefacc753d1f376ecb23ec9ee406fc7d972282d874f",
        ["reordered-keys"] = "be7ff915b37884d3e8d90cedc11ee818e9c40f3801c1258b6a172527a4fc5591",
        ["sorted-keys"] = "be7ff915b37884d3e8d90cedc11ee818e9c40f3801c1258b6a172527a4fc5591",
        ["paired-surrogate-escape"] = "813cbdff80cf683d3e400e8de673863ecc7fae8ae35d56a3d60b1a48d48bc2e5",
        ["literal-emoji"] = "813cbdff80cf683d3e400e8de673863ecc7fae8ae35d56a3d60b1a48d48bc2e5",
        ["escaped-accent"] = "72063a337bd8561cf333d6a8914bb5a0a6b93b029ebeface56baae04a1b75d44",
        ["literal-accent"] = "72063a337bd8561cf333d6a8914bb5a0a6b93b029ebeface56baae04a1b75d44",
        ["nested"] = "4a8da638c8cc77d8b3a48c5398bf3682462717716d70ae3bca9e363d703b6873",
        ["at-the-ceiling"] = "9a41e93ebd073141eb504d2597c5df59c7c935e7a90dccf6599ff6de397b0bf7",
        ["non-object"] = "1b8998c0fd8091c1935db41abc5a94fefd09de826936884dc0180228d3c3e132",
        ["no-schema"] = "9a189e9ad7b97c16defdb639e86aee17a5475394fd73ade94ee0f9bc59bb81e0",
        ["policy-plain"] = "be13f2fa16c8b1c919e26f5ec7c0806fcf7bd4867d2d4df36ea09a3da98628ef",
        ["policy-reordered"] = "be13f2fa16c8b1c919e26f5ec7c0806fcf7bd4867d2d4df36ea09a3da98628ef",
    };

    [Fact]
    public void Every_schema_the_aggregate_admits_hashes_to_the_byte_it_is_pinned_to()
    {
        Dictionary<string, string> actual = new(StringComparer.Ordinal);
        foreach ((var name, var schema) in Schemas)
        {
            actual[name] = VersionHashOf(schema);
        }

        actual["non-object"] = NonObjectVersionHash();
        actual["no-schema"] = NoSchemaVersionHash();
        actual["policy-plain"] = PolicyHashOf("""{"schemaVersion":1,"defaultTtl":"30s"}""");
        actual["policy-reordered"] = PolicyHashOf("""{"defaultTtl":"30s","schemaVersion":1}""");

        actual.ShouldBe(Pinned);
    }

    [Fact]
    public void The_canonical_form_still_earns_its_keep_while_the_bytes_stay_put()
    {
        // The falsifying half: pinning bytes proves nothing moved, and this
        // proves what did not move is still doing the work. Two spellings of
        // one document hash alike; two different documents do not.
        VersionHashOf("""{"b":2,"a":1}""").ShouldBe(VersionHashOf("""{"a":1,"b":2}"""));
        VersionHashOf($$"""{"v":"{{PairedSurrogateEscape}}"}""").ShouldBe(VersionHashOf("""{"v":"😀"}"""));
        VersionHashOf($$"""{"cidade":"S{{EscapedATilde}}o Paulo"}""")
            .ShouldBe(VersionHashOf("""{"cidade":"São Paulo"}"""));
        VersionHashOf("""{"a":1e2}""").ShouldNotBe(VersionHashOf("""{"a":100}"""));
    }

    /// <summary>
    /// Bytes one integrity verification allocated before the refusal moved,
    /// over the largest schema the aggregate admits, rounded up from five
    /// measured runs that spread over 153 bytes. The fence is a ceiling and not
    /// an equality: what it has to catch is a second canonical form produced
    /// per hash, which would roughly double this number, never a byte of
    /// jitter.
    /// </summary>
    private const long MaxAllocatedBytesPerVerification = 532_000;

    [Fact]
    public void One_integrity_verification_still_builds_the_canonical_form_once()
    {
        // Born green. The refusal is meant to wrap the traversal that already
        // ran, not to add one: a guard that canonicalizes to decide and then
        // canonicalizes again to hash costs twice the largest allocation on
        // this path, and it costs it on every publication and every rollback.
        AllocatedBytesPerVerification().ShouldBeLessThanOrEqualTo(MaxAllocatedBytesPerVerification);
    }

    /// <summary>A schema of exactly the number of characters the aggregate admits.</summary>
    private static string AtTheCeiling()
    {
        const string Prefix = "{\"properties\":{\"a\":{\"type\":\"string\",\"title\":\"";
        const string Suffix = "\"}}}";
        return Prefix + new string('x', TemplateVersion.MaxSchemaLength - Prefix.Length - Suffix.Length) + Suffix;
    }

    internal static string VersionHashOf(string schema)
    {
        TemplateVersion draft = DraftWithContent();
        draft.SetLayoutReference(LayoutKey.Create("corp.base").Value!, 3, "editor-1").IsSuccess.ShouldBeTrue();
        draft.SetVariablesSchema(schema, "editor-1").IsSuccess.ShouldBeTrue();
        return draft.ContentHash;
    }

    internal static string NoSchemaVersionHash() => DraftWithContent().ContentHash;

    internal static string NonObjectVersionHash()
        => TemplateVersion.Rehydrate(new TemplateVersionState
        {
            TemplateKey = Key.Value,
            Version = 1,
            Status = "published",
            CreatedBy = "author-1",
            CreatedAt = CreatedAt,
            VariablesSchemaJson = NonObjectSchema,
            Contents = [new TemplateContentState("email", "pt-BR", "Assunto", "corpo", null)],
        }).ContentHash;

    internal static string PolicyHashOf(string definition)
        => ClassPolicyVersion.CreateDraft(new ClassPolicyDraftInput
        {
            Application = "billing",
            Class = NotificationClasses.Create("transactional").Value,
            Version = 1,
            DefinitionJson = definition,
            CreatedBy = "author-1",
            CreatedAt = CreatedAt,
        }).Value!.ContentHash;

    internal static long AllocatedBytesPerVerification()
    {
        TemplateVersion version = DraftWithContent();
        version.SetVariablesSchema(AtTheCeiling(), "editor-1").IsSuccess.ShouldBeTrue();

        for (var warmup = 0; warmup < 32; warmup++)
        {
            version.VerifyContentHash();
        }

        const int Runs = 64;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var run = 0; run < Runs; run++)
        {
            version.VerifyContentHash();
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / Runs;
    }

    private static TemplateVersion DraftWithContent()
    {
        var draft = TemplateVersion.CreateDraft(Key, 1, "author-1", CreatedAt);
        draft.SetContent(
            new ContentEdit(
                Channel.Create("email").Value!,
                Locale.Create("pt-BR").Value!,
                "Assunto",
                "<p>{{code}}</p>",
                "{{code}}"),
            "editor-1").IsSuccess.ShouldBeTrue();
        return draft;
    }
}
