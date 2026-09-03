using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NotificationHub.Api.Modules.Notifications.Features.Ingress.RequestNotification;

namespace NotificationHub.UnitTests.Notifications;

/// <summary>
/// The manifest of attachment references inside the canonical form that
/// decides whether two requests are the same request. Files change what is
/// delivered, so a request that names them is not the request that names
/// none; a request that names none keeps the digest it has always had, which
/// is what lets a producer that never heard of the member keep retrying.
/// </summary>
public sealed class AttachmentManifestPayloadHashTests
{
    /// <summary>
    /// The digest of the request that asks for no attachment. It is the same
    /// value the request carried before the member existed, and it is what
    /// makes a retry sent by a producer that ignores the member a repetition
    /// rather than a conflict.
    /// </summary>
    private const string DigestOfNoManifest =
        "ae72ea096493d48cfcd34578542f2249b677872c5834778919df27af34cdbdeb";

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The bytes themselves, not only the digest they produce: the member
    /// enters between the application and the channel hint, and a reader of
    /// this test can see where. Moving it anywhere else leaves the request
    /// identical to a producer and changes the identity of every request that
    /// names a manifest.
    /// </summary>
    [Fact]
    public void The_canonical_bytes_carry_the_manifest_between_the_application_and_the_channel_hint()
    {
        RequestNotification.Command command = Minimal() with
        {
            ChannelsHint = ["email"],
            Attachments = ["att_alpha", "att_beta"],
        };
        const string canonical = """
            {"application":"araia-cambio","attachments":["att_alpha","att_beta"],"channelsHint":["email"],"class":"critical","recipientId":"cus_01J5X9","templateKey":"auth.otp.login","ttlSeconds":300}
            """;

        RequestNotification.ComputePayloadHash(command).ShouldBe(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
    }

    /// <summary>
    /// The rule the whole member exists for: asking for files is asking for
    /// something else. Without it, a second request that adds a manifest under
    /// the key of the first is answered with the first, and the producer is
    /// told a delivery it never asked for already happened.
    /// </summary>
    [Fact]
    public void A_request_that_names_a_manifest_is_not_the_request_that_names_none()
    {
        var withManifest = RequestNotification.ComputePayloadHash(
            Minimal() with { Attachments = ["att_alpha", "att_beta"] });

        withManifest.ShouldBe("5b707f0391c59c39cc4e0547f9f118d25a93d6bc4cbbe796191be44f0b4d8199");
        withManifest.ShouldNotBe(RequestNotification.ComputePayloadHash(Minimal()));
    }

    /// <summary>
    /// Three bodies that ask for no attachment: one that never names the
    /// member, one that writes it as null, and one that names it empty. They
    /// are one request, and they are the request whose digest predates the
    /// member. A client library that serializes an optional list as null must
    /// repeat the request it already sent, never conflict with it.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(""","attachments":null""")]
    [InlineData(""","attachments":[]""")]
    public void Every_way_of_asking_for_no_attachment_is_the_same_request(string manifestMember)
    {
        RequestNotification.Command command = Bind(
            $$"""
            {"application":"araia-cambio","recipientId":"cus_01J5X9","class":"critical",
             "templateKey":"auth.otp.login","ttlSeconds":300{{manifestMember}}}
            """);

        RequestNotification.ComputePayloadHash(command).ShouldBe(DigestOfNoManifest);
    }

    /// <summary>
    /// The order is the request. Two producers naming the same files in a
    /// different sequence are asking for two different deliveries, so the
    /// ingestion never sorts the manifest into a shape that would make them
    /// one.
    /// </summary>
    [Fact]
    public void The_order_of_the_manifest_is_part_of_the_request()
    {
        var inverted = RequestNotification.ComputePayloadHash(
            Minimal() with { Attachments = ["att_beta", "att_alpha"] });

        inverted.ShouldBe("97b013c55139f5fcd30a3d8685a3c326cde8984fcb232d74bd85552dc1fc789d");
        inverted.ShouldNotBe(RequestNotification.ComputePayloadHash(
            Minimal() with { Attachments = ["att_alpha", "att_beta"] }));
    }

    /// <summary>
    /// Swapping one reference for another asks for another file, which is
    /// another request even though the manifest keeps its length and its
    /// order.
    /// </summary>
    [Fact]
    public void Another_reference_makes_another_request()
    {
        var changed = RequestNotification.ComputePayloadHash(
            Minimal() with { Attachments = ["att_alpha", "att_gamma"] });

        changed.ShouldBe("71bb9b9799e73874e8eee914f91a2d0e1e9a69307437725073c02b7cb653c0dd");
        changed.ShouldNotBe(RequestNotification.ComputePayloadHash(
            Minimal() with { Attachments = ["att_alpha", "att_beta"] }));
    }

    /// <summary>
    /// Two spellings are two references. The ingestion is not the authority on
    /// what a reference names, so folding case or trimming here would answer
    /// one of two requests the issuing surface holds apart with the other.
    /// </summary>
    [Theory]
    [InlineData("att_ALPHA")]
    [InlineData("att_alpha ")]
    [InlineData(" att_alpha")]
    public void A_reference_written_in_another_spelling_is_another_request(string reference)
    {
        var other = RequestNotification.ComputePayloadHash(
            Minimal() with { Attachments = [reference] });

        other.ShouldNotBe(RequestNotification.ComputePayloadHash(
            Minimal() with { Attachments = ["att_alpha"] }));
    }

    /// <summary>
    /// The manifest carries no property of the file it names, so the members
    /// that travel with it stay where they were: the request that fills every
    /// optional member and asks for no attachment is the request it always
    /// was, and the one that asks for an attachment is not.
    /// </summary>
    [Fact]
    public void A_manifest_never_disturbs_the_members_that_travel_with_it()
    {
        RequestNotification.Command complete = Minimal() with
        {
            Locale = "pt-BR",
            Variables = JsonDocument.Parse("""{"code":"482913"}""").RootElement.Clone(),
            ChannelsHint = ["email", "sms"],
            CorrelationId = "trace-7c1e",
            Metadata = JsonDocument.Parse("""{"origin":"producer"}""").RootElement.Clone(),
            ScheduledAt = new DateTimeOffset(2026, 8, 23, 9, 0, 0, TimeSpan.FromHours(-3)),
        };

        RequestNotification.ComputePayloadHash(complete with { Attachments = [] })
            .ShouldBe(RequestNotification.ComputePayloadHash(complete));
        RequestNotification.ComputePayloadHash(complete with { Attachments = ["att_alpha"] })
            .ShouldNotBe(RequestNotification.ComputePayloadHash(complete));
    }

    private static RequestNotification.Command Minimal()
        => new(
            Application: "araia-cambio",
            RecipientId: "cus_01J5X9",
            Class: "critical",
            TemplateKey: "auth.otp.login",
            TtlSeconds: 300);

    private static RequestNotification.Command Bind(string body)
        => JsonSerializer.Deserialize<RequestNotification.Command>(body, Wire)
            ?? throw new InvalidOperationException("The body under test did not bind.");
}
