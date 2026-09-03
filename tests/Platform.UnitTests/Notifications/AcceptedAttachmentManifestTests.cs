using System.Text;
using System.Text.Json;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.UnitTests.Notifications;

/// <summary>
/// The stored attachment snapshot, read back. Three outcomes have to stay
/// three: the set, the notification that carries none, and a document nobody
/// can read. Folding the third into either of the other two is what turns a
/// corrupted row into a delivery over a composition the acceptance never
/// agreed to, or into a notification that quietly loses its attachments.
/// </summary>
public sealed class AcceptedAttachmentManifestTests
{
    private const string Whole = """
        {"schemaVersion":1,"items":[{"reference":"att_alpha","contentIdentity":"content_alpha","name":"arquivo.pdf","mediaType":"application/pdf","length":0}]}
        """;

    [Fact]
    public void A_column_that_was_never_written_reads_as_no_attachments()
        => AcceptedAttachmentManifest.Read(null).ShouldBeOfType<AcceptedManifestRead.Absent>();

    /// <summary>
    /// Blank text is not absence, and the difference is the whole reason the
    /// reader asks for the null value rather than for emptiness. The column
    /// holds a JSON document and the store refuses text that is not one, so
    /// blank text arriving here is a document nobody could have written.
    /// Answering absence would send the notification down the path with no
    /// attachments, which is the one answer a defect must never produce.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    public void Blank_text_is_a_defect_and_never_absence(string stored)
        => ShouldRefuse(stored, AcceptedAttachmentManifest.RefusedMalformedDocument);

    /// <summary>
    /// A body made only of whitespace control characters is the same
    /// defect, and it is built here rather than written as an escape so
    /// that the characters under test are the ones the assertion names.
    /// </summary>
    [Fact]
    public void A_body_of_whitespace_control_characters_is_a_defect()
        => ShouldRefuse(
            string.Concat((char)9, (char)10, (char)13),
            AcceptedAttachmentManifest.RefusedMalformedDocument);

    [Fact]
    public void A_whole_document_reads_back_as_the_set_it_froze()
    {
        AcceptedAttachmentSet accepted = ShouldRead(Whole);

        accepted.Count.ShouldBe(1);
        accepted[0].Reference.ShouldBe("att_alpha");
        accepted[0].ContentIdentity.ShouldBe("content_alpha");
        accepted[0].Name.ShouldBe("arquivo.pdf");
        accepted[0].MediaType.ShouldBe("application/pdf");
        accepted[0].Length.ShouldBe(0);
    }

    /// <summary>
    /// The order is part of the value, so it is asserted position by position
    /// rather than as a set. A reader that returned the members in any other
    /// order would still satisfy a comparison that only counted them.
    /// </summary>
    [Fact]
    public void The_order_the_set_was_claimed_in_survives_the_document()
    {
        AcceptedAttachmentSet accepted = ShouldRead(Two("att_first", "att_second"));

        accepted.Select(item => item.Reference).ShouldBe(["att_first", "att_second"]);
        ShouldRead(Two("att_second", "att_first"))
            .Select(item => item.Reference)
            .ShouldBe(["att_second", "att_first"]);
    }

    /// <summary>
    /// Every string is stored and returned exactly as the acceptance received
    /// it. A reader that trimmed, folded the case or normalised the code
    /// points would hand back a name, a media type or an identity that is not
    /// the one the release was granted over.
    /// </summary>
    [Fact]
    public void Every_string_survives_the_document_character_for_character()
    {
        // The same word in its composed and its decomposed spelling. The
        // arrangement is checked before it is used, because two spellings that
        // turned out to be one string would make the assertion below pass over
        // a reader that normalises everything it reads.
        const string Composed = "relatório café.pdf";
        var decomposed = Composed.Normalize(NormalizationForm.FormD);
        decomposed.Equals(Composed, StringComparison.Ordinal).ShouldBeFalse(
            "as duas grafias precisam diferir para que a asserção signifique algo.");

        AcceptedAttachmentSet accepted = ShouldRead($$"""
            {"schemaVersion":1,"items":[{"reference":" att_Alpha ","contentIdentity":"Content_Álpha","name":"{{decomposed}}","mediaType":"Application/PDF","length":7}]}
            """);

        accepted[0].Reference.ShouldBe(" att_Alpha ");
        accepted[0].ContentIdentity.ShouldBe("Content_Álpha");
        accepted[0].MediaType.ShouldBe("Application/PDF");

        // The document keeps the spelling it carries: neither trimmed, nor
        // folded to another case, nor recomposed into the other one.
        accepted[0].Name.Equals(decomposed, StringComparison.Ordinal).ShouldBeTrue(accepted[0].Name);
        accepted[0].Name.Equals(Composed, StringComparison.Ordinal).ShouldBeFalse(accepted[0].Name);
    }

    /// <summary>
    /// Two references are the same reference by ordinal comparison and by
    /// nothing else, which is the rule the set itself is built under. A reader
    /// that compared them without case would refuse a legitimate set.
    /// </summary>
    [Fact]
    public void Two_references_that_differ_only_in_case_are_two_references()
        => ShouldRead(Two("att_alpha", "ATT_ALPHA"))
            .Select(item => item.Reference)
            .ShouldBe(["att_alpha", "ATT_ALPHA"]);

    [Theory]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":1,\"items\":[]")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[{\"reference\":\"att_alpha\"}]")]
    [InlineData("\"a document\"")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("{}")]
    public void A_body_that_is_not_the_envelope_is_a_defect(string stored)
        => ShouldRefuse(stored, AcceptedAttachmentManifest.RefusedMalformedDocument);

    /// <summary>
    /// A member the envelope does not declare is refused rather than ignored.
    /// Ignoring it would read the document back as a set missing whatever that
    /// member was there to constrain, and the reader would report a
    /// composition that the writer never wrote.
    /// </summary>
    [Theory]
    [InlineData("""{"schemaVersion":1,"items":[ITEM],"expiresAt":"2026-09-03T00:00:00Z"}""")]
    [InlineData("""{"schemaVersion":1,"items":[{"reference":"att_alpha","contentIdentity":"content_alpha","name":"a.pdf","mediaType":"application/pdf","length":1,"storageKey":"bucket/key"}]}""")]
    public void A_member_the_envelope_does_not_declare_is_a_defect(string template)
        => ShouldRefuse(template.Replace("ITEM", Item("att_alpha"), StringComparison.Ordinal),
            AcceptedAttachmentManifest.RefusedMalformedDocument);

    /// <summary>
    /// The spelling of a member name is exact, case included. A reader that
    /// matched without case would accept a document written by something that
    /// is not this writer, and would then have to guess at everything else it
    /// spelled differently.
    /// </summary>
    [Theory]
    [InlineData("""{"SchemaVersion":1,"items":[ITEM]}""")]
    [InlineData("""{"schemaVersion":1,"Items":[ITEM]}""")]
    [InlineData("""{"schemaversion":1,"items":[ITEM]}""")]
    [InlineData("""{"schemaVersion":1,"items":[{"Reference":"att_alpha","contentIdentity":"content_alpha","name":"a.pdf","mediaType":"application/pdf","length":1}]}""")]
    public void A_member_name_spelled_with_another_case_is_a_defect(string template)
        => ShouldRefuse(template.Replace("ITEM", Item("att_alpha"), StringComparison.Ordinal),
            AcceptedAttachmentManifest.RefusedMalformedDocument);

    /// <summary>
    /// The same member twice is refused rather than resolved by taking one of
    /// them. Taking the last would read back a set the writer never wrote, and
    /// the document that carries the duplicate is not one this writer emits.
    /// </summary>
    [Fact]
    public void The_same_member_written_twice_is_a_defect()
        => ShouldRefuse(
            """{"schemaVersion":1,"items":[{"reference":"att_alpha","reference":"att_omega","contentIdentity":"content_alpha","name":"a.pdf","mediaType":"application/pdf","length":1}]}""",
            AcceptedAttachmentManifest.RefusedMalformedDocument);

    /// <summary>
    /// A version this code does not know is refused with a word of its own.
    /// Reading it would mean guessing what the members this reader cannot see
    /// were there to constrain, and the operational answer to a version nobody
    /// deployed a reader for is not the answer to a corrupted document.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void A_version_this_reader_does_not_know_is_refused_by_version(int version)
        => ShouldRefuse(
            $$"""{"schemaVersion":{{version}},"items":[{{Item("att_alpha")}}]}""",
            AcceptedAttachmentManifest.RefusedUnknownSchemaVersion);

    [Theory]
    [InlineData("null")]
    [InlineData("\"1\"")]
    [InlineData("1.5")]
    [InlineData("[1]")]
    [InlineData("9223372036854775808")]
    public void A_version_that_is_not_a_whole_number_is_a_defect(string version)
        => ShouldRefuse(
            $$"""{"schemaVersion":{{version}},"items":[{{Item("att_alpha")}}]}""",
            AcceptedAttachmentManifest.RefusedMalformedDocument);

    /// <summary>
    /// A member that is absent is a defect and never a default. Without this,
    /// a document that lost its length would read back as a set whose members
    /// are zero bytes long, and the reader would report a composition nobody
    /// accepted rather than a document nobody can read.
    /// </summary>
    [Theory]
    [InlineData("""{"items":[ITEM]}""")]
    [InlineData("""{"schemaVersion":1}""")]
    [InlineData("""{"schemaVersion":1,"items":[{"contentIdentity":"content_alpha","name":"a.pdf","mediaType":"application/pdf","length":1}]}""")]
    [InlineData("""{"schemaVersion":1,"items":[{"reference":"att_alpha","name":"a.pdf","mediaType":"application/pdf","length":1}]}""")]
    [InlineData("""{"schemaVersion":1,"items":[{"reference":"att_alpha","contentIdentity":"content_alpha","mediaType":"application/pdf","length":1}]}""")]
    [InlineData("""{"schemaVersion":1,"items":[{"reference":"att_alpha","contentIdentity":"content_alpha","name":"a.pdf","length":1}]}""")]
    [InlineData("""{"schemaVersion":1,"items":[{"reference":"att_alpha","contentIdentity":"content_alpha","name":"a.pdf","mediaType":"application/pdf"}]}""")]
    public void A_member_the_envelope_declares_and_the_document_omits_is_a_defect(string template)
        => ShouldRefuse(template.Replace("ITEM", Item("att_alpha"), StringComparison.Ordinal),
            AcceptedAttachmentManifest.RefusedMalformedDocument);

    [Theory]
    [InlineData("""{"schemaVersion":1,"items":[{"reference":1,"contentIdentity":"content_alpha","name":"a.pdf","mediaType":"application/pdf","length":1}]}""")]
    [InlineData("""{"schemaVersion":1,"items":[{"reference":"att_alpha","contentIdentity":"content_alpha","name":"a.pdf","mediaType":"application/pdf","length":"1"}]}""")]
    [InlineData("""{"schemaVersion":1,"items":[{"reference":"att_alpha","contentIdentity":"content_alpha","name":"a.pdf","mediaType":"application/pdf","length":1.5}]}""")]
    [InlineData("""{"schemaVersion":1,"items":[{"reference":"att_alpha","contentIdentity":"content_alpha","name":"a.pdf","mediaType":"application/pdf","length":9223372036854775808}]}""")]
    [InlineData("""{"schemaVersion":1,"items":{"reference":"att_alpha"}}""")]
    [InlineData("""{"schemaVersion":1,"items":"att_alpha"}""")]
    public void A_member_carrying_another_type_is_a_defect(string stored)
        => ShouldRefuse(stored, AcceptedAttachmentManifest.RefusedMalformedDocument);

    /// <summary>
    /// A set that cannot exist is refused by the same authority the claim is
    /// held to. Stating the rules again here would be a second authority, free
    /// to drift away from the one that decides which sets a claim may accept.
    /// </summary>
    [Theory]
    [InlineData("""{"schemaVersion":1,"items":null}""")]
    [InlineData("""{"schemaVersion":1,"items":[]}""")]
    [InlineData("""{"schemaVersion":1,"items":[null]}""")]
    [InlineData("""{"schemaVersion":1,"items":[{"reference":"","contentIdentity":"content_alpha","name":"a.pdf","mediaType":"application/pdf","length":1}]}""")]
    [InlineData("""{"schemaVersion":1,"items":[{"reference":"   ","contentIdentity":"content_alpha","name":"a.pdf","mediaType":"application/pdf","length":1}]}""")]
    [InlineData("""{"schemaVersion":1,"items":[{"reference":"att_alpha","contentIdentity":null,"name":"a.pdf","mediaType":"application/pdf","length":1}]}""")]
    [InlineData("""{"schemaVersion":1,"items":[{"reference":"att_alpha","contentIdentity":"content_alpha","name":"","mediaType":"application/pdf","length":1}]}""")]
    [InlineData("""{"schemaVersion":1,"items":[{"reference":"att_alpha","contentIdentity":"content_alpha","name":"a.pdf","mediaType":null,"length":1}]}""")]
    [InlineData("""{"schemaVersion":1,"items":[{"reference":"att_alpha","contentIdentity":"content_alpha","name":"a.pdf","mediaType":"application/pdf","length":-1}]}""")]
    public void A_set_that_could_never_have_been_claimed_is_refused_as_unusable(string stored)
        => ShouldRefuse(stored, AcceptedAttachmentManifest.RefusedUnusableSet);

    [Fact]
    public void The_same_reference_twice_is_refused_as_unusable()
        => ShouldRefuse(Two("att_alpha", "att_alpha"),
            AcceptedAttachmentManifest.RefusedUnusableSet);

    /// <summary>
    /// The document the writer emits is the envelope this reader is specified
    /// against, member by member. A round trip alone would pass over any pair
    /// of names, as long as the writer and the reader agreed on them.
    /// </summary>
    [Fact]
    public void The_written_document_names_the_envelope_and_nothing_else()
    {
        using JsonDocument written = JsonDocument.Parse(AcceptedAttachmentManifest.Serialize(
            AcceptedAttachmentSet.Of([Attachment("att_alpha")])));

        JsonElement root = written.RootElement;
        root.ValueKind.ShouldBe(JsonValueKind.Object);
        root.EnumerateObject().Select(member => member.Name).ShouldBe(["schemaVersion", "items"]);
        root.GetProperty("schemaVersion").GetInt32().ShouldBe(1);
        root.GetProperty("items").EnumerateArray().Single()
            .EnumerateObject().Select(member => member.Name)
            .Order(StringComparer.Ordinal)
            .ShouldBe(["contentIdentity", "length", "mediaType", "name", "reference"]);
    }

    /// <summary>
    /// The written document carries nothing that could be exchanged for the
    /// content. The check is over the raw text rather than over the member
    /// names, because a leak that reused a declared member would pass a name
    /// check untouched.
    /// </summary>
    [Fact]
    public void The_written_document_carries_nothing_that_reaches_the_content()
    {
        var written = AcceptedAttachmentManifest.Serialize(AcceptedAttachmentSet.Of([Attachment("att_alpha")]));

        foreach (var forbidden in new[] { "bucket", "digest", "sha", "http", "key", "arn:", "VersionId" })
        {
            written.Contains(forbidden, StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
                $"o documento durável não pode carregar '{forbidden}': {written}");
        }
    }

    /// <summary>
    /// A refusal names the shape of the defect and never an instance of it.
    /// The values planted here are producer data, and a refusal that quoted
    /// one would publish, on the operational side, exactly what the acceptance
    /// path keeps out of its answers and its log lines.
    /// </summary>
    [Fact]
    public void A_refusal_never_quotes_the_document_it_refused()
    {
        const string PlantedReference = "att_cpf_do_titular_98765";
        const string PlantedName = "contrato-maria-silva.pdf";

        var refusal = ShouldRefuse(
            $$"""
              {"schemaVersion":9,"items":[{"reference":"{{PlantedReference}}","contentIdentity":"content_alpha","name":"{{PlantedName}}","mediaType":"application/pdf","length":11}]}
              """,
            AcceptedAttachmentManifest.RefusedUnknownSchemaVersion);

        refusal.Contains(PlantedReference, StringComparison.Ordinal).ShouldBeFalse(refusal);
        refusal.Contains(PlantedName, StringComparison.Ordinal).ShouldBeFalse(refusal);
        AcceptedAttachmentManifest.Refusals.ShouldContain(refusal);
    }

    private static AcceptedAttachmentSet ShouldRead(string stored)
        => AcceptedAttachmentManifest.Read(stored)
            .ShouldBeOfType<AcceptedManifestRead.Present>()
            .Accepted;

    private static string ShouldRefuse(string stored, string reason)
    {
        var refusal = AcceptedAttachmentManifest.Read(stored)
            .ShouldBeOfType<AcceptedManifestRead.Unreadable>()
            .Reason;

        refusal.ShouldBe(reason);
        return refusal;
    }

    private static AcceptedAttachment Attachment(string reference) => new()
    {
        Reference = reference,
        ContentIdentity = "content_" + reference,
        Name = reference + ".pdf",
        MediaType = "application/pdf",
        Length = 11,
    };

    private static string Item(string reference)
        => $$"""{"reference":"{{reference}}","contentIdentity":"content_alpha","name":"a.pdf","mediaType":"application/pdf","length":1}""";

    private static string Two(string first, string second)
        => $$"""
           {"schemaVersion":1,"items":[{"reference":"{{first}}","contentIdentity":"content_{{first}}","name":"{{first}}.pdf","mediaType":"application/pdf","length":1},{"reference":"{{second}}","contentIdentity":"content_{{second}}","name":"{{second}}.pdf","mediaType":"application/pdf","length":2}]}
           """;
}
