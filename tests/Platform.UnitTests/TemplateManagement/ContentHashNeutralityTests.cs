using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// Born green, and it is the fence the rest of this work stands on. Refusing a
/// payload that does not transcode moved where the canonical form is produced;
/// it must not have moved a single byte of what the form produces. Every hash
/// here is pinned to the value the aggregates answered before that move: a
/// hash that shifts turns every published version into an integrity failure,
/// which would declare tampering in bulk over content nobody touched.
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
    /// a row written before the door existed still has to hash to what it
    /// always hashed to, or verifying it would report tampering.
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
    /// The hash each corpus entry produced before the refusal moved. The values
    /// are literals and not a recomputation on purpose: a test that recomputes
    /// both sides with the same code cannot tell that the code changed.
    /// </summary>
    private static readonly Dictionary<string, string> Pinned = new(StringComparer.Ordinal)
    {
        ["exponent"] = "c787e4b2cdbdb45f54b3c88a1376c6fa02e281f9545691cdb6b680ad8bffd85b",
        ["trailing-zeros"] = "0b5de60f0b47fe9ea3db92645795c746b03b6dd701688edaeff25c72057a9447",
        ["decimal-zero"] = "2b70dbb1ad6d89a10daa59456b2ae8b87db747d31747083466c7bcb06b653213",
        ["negative-zero"] = "e0586be3b42e424a9509b7a18b0c2212e57b66d3a51ff670f0ddae503510ad46",
        ["reordered-keys"] = "2ab0164b5d4bd10bced2beb302dae14f812636889286fb23750be6bffa7c36c9",
        ["sorted-keys"] = "2ab0164b5d4bd10bced2beb302dae14f812636889286fb23750be6bffa7c36c9",
        ["paired-surrogate-escape"] = "cd889df9425936a9f1fa0c36f6fa6435ff5a20e76cfbcb8983a36bd4d96fe9e4",
        ["literal-emoji"] = "cd889df9425936a9f1fa0c36f6fa6435ff5a20e76cfbcb8983a36bd4d96fe9e4",
        ["escaped-accent"] = "06bf2fe8ef343cd0665be20a6479cc3a3bec08b0f1053672a7cd38439194049d",
        ["literal-accent"] = "06bf2fe8ef343cd0665be20a6479cc3a3bec08b0f1053672a7cd38439194049d",
        ["nested"] = "e2d5ccf3e035e081a50f561d77b0b959af025d5154d055d5843d59a6e9f351f3",
        ["at-the-ceiling"] = "64786502d65d30e210e959b68d141af2668f56a93eb03a2f608b06fa3c01a230",
        ["non-object"] = "2b21c9fcd440807da5db1416d1eb7403afba15f790f533307a3c8dd3a4d72507",
        ["no-schema"] = "e112db8d6d1a9710c0fc13ef6bb416e41814d46980165c2181c08bba43a33e23",
        ["policy-plain"] = "be13f2fa16c8b1c919e26f5ec7c0806fcf7bd4867d2d4df36ea09a3da98628ef",
        ["policy-reordered"] = "be13f2fa16c8b1c919e26f5ec7c0806fcf7bd4867d2d4df36ea09a3da98628ef",
    };

    [Fact]
    public void Every_schema_the_aggregate_admits_hashes_to_the_byte_it_always_hashed_to()
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
