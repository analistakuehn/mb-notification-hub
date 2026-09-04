using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

namespace NotificationHub.UnitTests.AttachmentManagement;

/// <summary>
/// The handle that lets a snapshot outside this module say which bytes were
/// accepted without carrying what proves them.
/// <para>
/// The whole design rests on one asymmetry: the handle travels, and resolving
/// it does not. A consumer that holds one can hand it back and be told a
/// verdict; it cannot compare it against anything, derive anything from it, or
/// reach the content with it. What these rules hold is that end of the
/// asymmetry, which is the end this module owns.
/// </para>
/// </summary>
public sealed class AttachmentContentIdentityTests
{
    private static readonly DateTimeOffset CapturedAt = DateTimeOffset.Parse(
        "2026-09-02T12:00:00Z",
        CultureInfo.InvariantCulture);

    [Fact]
    public void A_handle_names_the_generation_it_was_minted_for()
    {
        AttachmentObjectGeneration generation = Captured();

        var handle = AttachmentContentIdentity.For(generation);

        AttachmentContentIdentity.GenerationOf(handle).ShouldBe(generation.Id);
        handle.Length.ShouldBe(AttachmentContentIdentity.Length);
    }

    /// <summary>
    /// The handle minted from the row and the handle minted from its
    /// identifier are one handle.
    /// <para>
    /// The reader that never materializes the row holds only the identifier,
    /// and it mints from that. The rule that says a handle carries nothing
    /// else from the row is written against the form that is handed the row,
    /// so this equality is what carries that rule over to the other form: two
    /// spellings that agree cannot drift, and one of them is already covered.
    /// </para>
    /// </summary>
    [Fact]
    public void A_handle_is_the_same_whether_it_is_minted_from_the_row_or_from_its_identifier()
    {
        AttachmentObjectGeneration generation = Captured();

        AttachmentContentIdentity.For(generation.Id)
            .ShouldBe(AttachmentContentIdentity.For(generation));
    }

    /// <summary>
    /// Two generations are two handles. A handle shared by two would say that
    /// two different captures are the same content, and the snapshot that
    /// carries it would stop being able to say which bytes were accepted,
    /// which is the one thing it exists to say.
    /// </summary>
    [Fact]
    public void Two_generations_are_never_named_by_one_handle()
    {
        AttachmentObjectGeneration first = Captured();
        AttachmentObjectGeneration second = Captured();

        AttachmentContentIdentity.For(first)
            .ShouldNotBe(AttachmentContentIdentity.For(second));
    }

    /// <summary>
    /// The handle is not the record. It is minted from the row it names, and
    /// what it must not carry is anything else on that row: not the store, not
    /// the key, not the generation the provider named, and above all not the
    /// digest, in any of the spellings it travels in.
    /// <para>
    /// The digest is the reason this rule exists. A handle that carried it
    /// would publish, to every consumer and into every message and log line
    /// that touches a snapshot, the one value that proves which bytes these
    /// are, and it would publish it in a form nothing can take back.
    /// </para>
    /// </summary>
    [Fact]
    public void A_handle_carries_nothing_else_from_the_record_it_names()
    {
        AttachmentObjectGeneration generation = Captured();

        var handle = AttachmentContentIdentity.For(generation);

        string[] prohibited =
        [
            generation.Store,
            generation.Key,
            generation.Version,
            generation.Algorithm,
            Convert.ToHexString(generation.Digest),
            Convert.ToHexString(generation.Digest).ToLowerInvariant(),
            Convert.ToBase64String(generation.Digest),
        ];

        // Read in both directions on purpose. Asking only whether the handle
        // carries the whole value passes a handle built from part of one, and
        // half a digest is the part that matters: it is still a statement
        // about the bytes and it still travels.
        var body = handle[AttachmentContentIdentity.Prefix.Length..];
        foreach (var value in prohibited)
        {
            handle.Contains(value, StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
                "um identificador de conteúdo não transporta nada da linha que ele nomeia.");
            value.Contains(body, StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
                "um identificador de conteúdo não é um pedaço da linha que ele nomeia.");
        }

        // The prohibited values have to be present in the arrangement before
        // their absence in the handle says anything: a row whose coordinates
        // were blank would satisfy every check above by carrying nothing.
        prohibited.ShouldAllBe(value => value.Length > 0);
    }

    /// <summary>
    /// The two vocabularies this module hands the same consumer. One names an
    /// attachment and the other names the bytes it was accepted with, and the
    /// prefixes are what keep a caller from handing back the wrong one and
    /// being answered as though it were the right one.
    /// </summary>
    [Fact]
    public void The_reference_of_an_attachment_names_no_generation()
    {
        var reference = AttachmentReference.Generate();

        AttachmentContentIdentity.GenerationOf(reference.Value).ShouldBeNull();
        AttachmentContentIdentity.Prefix.ShouldNotBe(AttachmentReference.Prefix);
    }

    /// <summary>
    /// Text that was not minted here names no generation, whatever is wrong
    /// with it. The identifier alone is the case that matters: it is what a
    /// caller would produce by stripping the prefix, and answering it would
    /// make the prefix decoration instead of the thing that tells the two
    /// vocabularies apart.
    /// </summary>
    [Fact]
    public void Text_that_was_not_minted_here_names_no_generation()
    {
        AttachmentObjectGeneration generation = Captured();
        var handle = AttachmentContentIdentity.For(generation);

        AttachmentContentIdentity.GenerationOf(null).ShouldBeNull();
        AttachmentContentIdentity.GenerationOf("").ShouldBeNull();
        AttachmentContentIdentity.GenerationOf(generation.Id.ToString("N")).ShouldBeNull();
        AttachmentContentIdentity.GenerationOf(generation.Id.ToString("D")).ShouldBeNull();
        AttachmentContentIdentity.GenerationOf("xxx_" + generation.Id.ToString("N")).ShouldBeNull();
        AttachmentContentIdentity.GenerationOf(AttachmentContentIdentity.Prefix + "not-an-identifier")
            .ShouldBeNull();
        AttachmentContentIdentity.GenerationOf(handle + "0").ShouldBeNull();
        AttachmentContentIdentity.GenerationOf(handle[..^1]).ShouldBeNull();

        // The neighbour of every refusal above, so that none of them can grow
        // into a rule that refuses everything.
        AttachmentContentIdentity.GenerationOf(handle).ShouldBe(generation.Id);
    }

    private static AttachmentObjectGeneration Captured()
        => AttachmentObjectGeneration.Capture(
            Guid.CreateVersion7(),
            AttachmentObjectLocator.FromStoredRow(
                "attachment-store",
                "attachments/" + Guid.NewGuid().ToString("N"),
                "generation-" + Guid.NewGuid().ToString("N")),
            AttachmentContentProof.Sha256Of(
                SHA256.HashData(Encoding.UTF8.GetBytes("attachment-content")),
                lengthBytes: 42),
            detectedContentType: "application/pdf",
            CapturedAt);
}
