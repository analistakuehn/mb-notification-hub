using System.Text.Json;
using FluentValidation.Results;
using NotificationHub.Api.Modules.Notifications.Features.Ingress;
using NotificationHub.Api.Modules.Notifications.Features.Ingress.RequestNotification;

namespace NotificationHub.UnitTests.Notifications.Ingress;

/// <summary>
/// The manifest of attachment references as the bus transport reads it. The
/// binder is the only place where a published body becomes a command on this
/// transport, so a member it does not read is a member the producer sent and
/// the hub never received: the body still binds, the request is still
/// accepted, and the acceptance answers for a delivery nobody asked for.
/// </summary>
public sealed class IngressManifestBindingTests
{
    private static readonly RequestNotification.Validator Validator = new();

    /// <summary>
    /// The body of a producer that never heard of attachments. Every case
    /// below holds it fixed and moves the manifest alone against it.
    /// </summary>
    private const string BodyWithoutManifest =
        """
        {
          "application": "araia-cambio",
          "recipientId": "cus_01J5X9",
          "idempotencyKey": "key-manifest",
          "class": "critical",
          "templateKey": "auth.otp.login",
          "ttlSeconds": 300
        }
        """;

    /// <summary>
    /// The references arrive as the producer spelled them and in the sequence
    /// it chose. The ingestion is not the authority on what a reference names,
    /// so sorting, trimming or folding case here would hand the use case a
    /// manifest nobody published.
    /// </summary>
    [Fact]
    public void A_published_manifest_is_bound_in_the_sequence_and_the_spelling_it_arrived_in()
    {
        IngressRequest request = BindOrFail("""["att_beta", "att_ALPHA", "att_alpha "]""");

        request.Command.Attachments.ShouldBe(["att_beta", "att_ALPHA", "att_alpha "]);
    }

    /// <summary>
    /// The window this closes, and the reason the member had to be read at
    /// all: while the manifest was dropped, a body that asked for files and a
    /// body that asked for none were the same request. Under one idempotency
    /// key the second was answered with the acceptance of the first, so the
    /// producer was told a delivery carrying its files had happened and none
    /// ever existed.
    /// </summary>
    [Fact]
    public void A_published_manifest_changes_the_identity_of_the_request_that_carried_it()
    {
        IngressRequest withManifest = BindOrFail("""["att_alpha"]""");
        IngressRequest withoutManifest = BindOrFail(manifestJson: null);

        RequestNotification.ComputePayloadHash(withManifest.Command)
            .ShouldNotBe(RequestNotification.ComputePayloadHash(withoutManifest.Command));
    }

    /// <summary>
    /// The falsifying half of the rule above: everything else about the two
    /// requests is the same request, so the digests part over the manifest and
    /// not over some other difference the two bodies happen to carry.
    /// </summary>
    [Fact]
    public void Two_bodies_that_carry_the_same_manifest_are_the_same_request()
    {
        IngressRequest first = BindOrFail("""["att_alpha", "att_beta"]""");
        IngressRequest second = BindOrFail("""["att_alpha", "att_beta"]""");

        RequestNotification.ComputePayloadHash(second.Command)
            .ShouldBe(RequestNotification.ComputePayloadHash(first.Command));
    }

    /// <summary>
    /// A member that is missing and a member written as JSON null are the same
    /// legal request without attachments. A client library that serializes an
    /// optional list as null is retrying the request it already sent and is
    /// owed the answer of that request, never a conflict.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public void A_manifest_that_is_absent_or_written_as_null_binds_as_a_request_without_one(
        string? manifestJson)
    {
        IngressRequest request = BindOrFail(manifestJson);

        request.Command.Attachments.ShouldBeNull();
        Validator.Validate(request.Command).IsValid.ShouldBeTrue();
    }

    /// <summary>
    /// An empty list is a producer asking for attachments and naming none,
    /// which is a different request from one that never asked. Binding it as
    /// absence would answer it with an acceptance instead of the refusal the
    /// shared validator owes it, so the emptiness survives the binder.
    /// </summary>
    [Fact]
    public void An_empty_manifest_survives_the_binder_so_the_shared_validator_can_refuse_it()
    {
        IngressRequest request = BindOrFail("[]");

        request.Command.Attachments.ShouldNotBeNull().Count.ShouldBe(0);
        ValidationResult result = Validator.Validate(request.Command);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().PropertyName.ShouldBe("Attachments");
    }

    /// <summary>
    /// A reference that names nothing and a reference the producer asked for
    /// twice are transported and then refused, because the rule that answers
    /// them is the shared one and belongs to no transport.
    /// </summary>
    [Theory]
    [InlineData("""["att_alpha", ""]""")]
    [InlineData("""["att_alpha", "   "]""")]
    [InlineData("""["att_alpha", "att_alpha"]""")]
    public void A_manifest_the_shared_rules_refuse_reaches_them_through_this_transport(
        string manifestJson)
    {
        IngressRequest request = BindOrFail(manifestJson);

        ValidationResult result = Validator.Validate(request.Command);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().PropertyName.ShouldBe("Attachments");
    }

    /// <summary>
    /// A manifest whose JSON shape cannot be a list of references is refused
    /// here, where the other optional members of the contract are refused for
    /// the same reason. The refusal is the one a malformed body already takes,
    /// so the record dies alone and the partition keeps moving.
    /// </summary>
    [Theory]
    [InlineData("\"att_alpha\"")]
    [InlineData("7")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("""{"0": "att_alpha"}""")]
    [InlineData("[7]")]
    [InlineData("[null]")]
    [InlineData("""[["att_alpha"]]""")]
    [InlineData("""[{"reference": "att_alpha"}]""")]
    public void A_manifest_whose_json_shape_is_not_a_list_of_references_is_refused(string manifestJson)
        => Bind(manifestJson).ShouldBeNull();

    /// <summary>
    /// The rule the contract states, in the form that outlives this
    /// implementation: a surface either carries the member or refuses the body
    /// that names it. A binder that bound the body and dropped the member
    /// would satisfy neither answer, and that is the one failure this rule
    /// exists to forbid, because it keeps the syntax valid and changes the
    /// effect. The body without the member is bound here too, so a refusal
    /// above is a refusal of the manifest and not of every record on the topic.
    /// </summary>
    [Fact]
    public void A_body_that_names_a_manifest_is_carried_or_refused_and_never_bound_without_it()
    {
        IngressRequest? named = Bind("""["att_alpha", "att_beta"]""");
        IngressRequest? unnamed = Bind(manifestJson: null);

        unnamed.ShouldNotBeNull().Command.Attachments.ShouldBeNull();
        var carriedOrRefused = named is null
            || named.Command.Attachments is { Count: 2 };
        carriedOrRefused.ShouldBeTrue(
            "O corpo que nomeia o manifesto foi vinculado sem ele, portanto o "
            + "produtor recebe o aceite de uma notificação sem os anexos que pediu.");
    }

    private static IngressRequest BindOrFail(string? manifestJson)
        => Bind(manifestJson).ShouldNotBeNull();

    private static IngressRequest? Bind(string? manifestJson)
    {
        using var document = JsonDocument.Parse(BodyOf(manifestJson));
        return IngressRequestBinder.Bind(document.RootElement);
    }

    /// <summary>
    /// The published body, with the manifest spliced in as raw JSON text. Raw
    /// on purpose: the cases below are about JSON shapes a typed builder could
    /// not express, and a body assembled from objects would only ever produce
    /// the shapes that already bind.
    /// </summary>
    private static string BodyOf(string? manifestJson)
        => manifestJson is null
            ? BodyWithoutManifest
            : BodyWithoutManifest.Replace(
                "\"ttlSeconds\": 300",
                $"\"ttlSeconds\": 300,\n  \"attachments\": {manifestJson}",
                StringComparison.Ordinal);
}
