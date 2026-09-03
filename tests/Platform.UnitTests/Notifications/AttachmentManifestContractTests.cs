using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using FluentValidation.Results;
using NotificationHub.Api.Modules.Notifications.Features.Ingress.RequestNotification;

namespace NotificationHub.UnitTests.Notifications;

/// <summary>
/// The manifest of attachment references inside the request contract of the
/// ingestion. It is one contract and the manifest is optional in it: a request
/// that names no manifest is the request every producer already sends, and a
/// request that names one is asking for the files it names.
/// </summary>
public sealed class AttachmentManifestContractTests
{
    /// <summary>
    /// The members the published body names, in the order the exporter writes
    /// them. Frozen here because it is the body producers generate their
    /// clients from, and a member leaving it takes with it every request that
    /// was written against it.
    /// </summary>
    private static readonly string[] PublishedMembers =
    [
        "application",
        "recipientId",
        "class",
        "templateKey",
        "ttlSeconds",
        "locale",
        "variables",
        "channelsHint",
        "correlationId",
        "metadata",
        "scheduledAt",
        "attachments",
    ];

    private static readonly RequestNotification.Validator Validator = new();

    /// <summary>
    /// The reader the route uses: web defaults, which match member names
    /// without regard to case. The resolver is named because the schema
    /// exporter refuses options that carry none.
    /// </summary>
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>
    /// Read from the type rather than from the published document: the
    /// document names each body by the short name of its type, so a body that
    /// shares that name with another answers for it and would report the wrong
    /// members here. The type is the subject of this rule, so the type is what
    /// it reads.
    /// </summary>
    [Fact]
    public void The_published_body_of_the_ingestion_names_the_manifest()
    {
        JsonNode schema = Wire.GetJsonSchemaAsNode(typeof(RequestNotification.Command));

        var members = schema["properties"]
            .ShouldNotBeNull()
            .AsObject()
            .Select(member => member.Key)
            .ToArray();

        members.ShouldContain(
            "attachments",
            "O corpo publicado da ingestão deixou de nomear o manifesto, então "
            + "nenhum cliente gerado a partir dele consegue pedir anexos.");
        members.ShouldBe(PublishedMembers);
    }

    /// <summary>
    /// The manifest crosses from the body into the use case exactly as it
    /// arrived. Order, case and repetition are all preserved: each of them is
    /// what tells two requests apart, and a binding that sorted, folded or
    /// deduplicated would hand the use case a request the producer never made.
    /// </summary>
    [Fact]
    public void A_manifest_binds_in_the_order_and_the_spelling_it_arrived_in()
    {
        RequestNotification.Command command = Bind(
            """
            {"application":"araia-cambio","recipientId":"cus_01J5X9","class":"critical",
             "templateKey":"auth.otp.login","ttlSeconds":300,
             "attachments":["att_beta","att_ALPHA","att_alpha","att_beta"]}
            """);

        command.Attachments.ShouldBe(["att_beta", "att_ALPHA", "att_alpha", "att_beta"]);
    }

    /// <summary>
    /// The reader matches every other member without regard to case, so a
    /// producer writing this one in another case is naming this member. A
    /// binding that missed it would accept the request, drop the manifest and
    /// tell the producer a delivery it never asked for happened.
    /// </summary>
    [Theory]
    [InlineData("Attachments")]
    [InlineData("ATTACHMENTS")]
    public void A_manifest_named_in_another_case_binds_the_same_way(string member)
    {
        RequestNotification.Command command = Bind(
            $$"""
            {"application":"araia-cambio","recipientId":"cus_01J5X9","class":"critical",
             "templateKey":"auth.otp.login","ttlSeconds":300,"{{member}}":["att_alpha"]}
            """);

        command.Attachments.ShouldBe(["att_alpha"]);
    }

    /// <summary>
    /// A member the contract does not name is carried past the binding and
    /// ignored, so a producer may send a field a later contract will name
    /// without being refused today.
    /// </summary>
    [Fact]
    public void An_unrelated_member_the_contract_does_not_name_is_still_accepted()
    {
        RequestNotification.Command command = Bind(
            """
            {"application":"araia-cambio","recipientId":"cus_01J5X9","class":"critical",
             "templateKey":"auth.otp.login","ttlSeconds":300,"deliveryWindow":{"from":"08:00"}}
            """);

        Validator.Validate(command).IsValid.ShouldBeTrue();
    }

    /// <summary>
    /// A request that never named a manifest carries no manifest rule at all,
    /// so nothing added for attachments can refuse a request that has none.
    /// </summary>
    [Fact]
    public void A_request_without_a_manifest_meets_no_manifest_rule()
    {
        var command = new RequestNotification.Command(
            "araia-cambio", "cus_01J5X9", "critical", "auth.otp.login", 300);

        command.Attachments.ShouldBeNull();
        Validator.Validate(command).IsValid.ShouldBeTrue();
    }

    /// <summary>
    /// A manifest written as JSON null is a request that names none, and is
    /// the same legal request as one that omits the member. Only the empty
    /// list is a producer asking for attachments and naming no reference.
    /// </summary>
    [Fact]
    public void A_manifest_written_as_null_is_a_request_without_one()
    {
        RequestNotification.Command command = Bind(
            """
            {"application":"araia-cambio","recipientId":"cus_01J5X9","class":"critical",
             "templateKey":"auth.otp.login","ttlSeconds":300,"attachments":null}
            """);

        command.Attachments.ShouldBeNull();
        Validator.Validate(command).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_manifest_that_names_no_reference_is_refused()
    {
        ValidationResult result = Validator.Validate(WithAttachments([]));

        result.IsValid.ShouldBeFalse();
        ValidationFailure failure = result.Errors.ShouldHaveSingleItem();
        failure.PropertyName.ShouldBe("Attachments");
        failure.ErrorMessage.ShouldBe("Attachments must name at least one attachment reference.");
    }

    [Fact]
    public void A_manifest_that_repeats_a_reference_is_refused()
    {
        ValidationResult result = Validator.Validate(
            WithAttachments(["att_alpha", "att_beta", "att_alpha"]));

        result.IsValid.ShouldBeFalse();
        ValidationFailure failure = result.Errors.ShouldHaveSingleItem();
        failure.PropertyName.ShouldBe("Attachments");
        failure.ErrorMessage.ShouldBe("Attachments must not repeat the same attachment reference.");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void A_reference_that_names_nothing_is_refused(string reference)
    {
        ValidationResult result = Validator.Validate(WithAttachments(["att_alpha", reference]));

        result.IsValid.ShouldBeFalse();
        ValidationFailure failure = result.Errors.ShouldHaveSingleItem();
        failure.PropertyName.ShouldBe("Attachments");
        failure.ErrorMessage.ShouldBe("Attachments must not carry a reference that names nothing.");
    }

    /// <summary>
    /// Two spellings are two references. The ingestion is not the authority on
    /// what a reference names, so folding case or trimming here would refuse a
    /// pair the issuing surface holds apart.
    /// </summary>
    [Theory]
    [InlineData("att_alpha", "att_ALPHA")]
    [InlineData("att_alpha", "att_alpha ")]
    public void References_that_differ_only_in_spelling_are_two_references(string first, string second)
        => Validator.Validate(WithAttachments([first, second])).IsValid.ShouldBeTrue();

    /// <summary>
    /// A manifest is refused by one sentence per rule and never by one per
    /// position. The request is refused for its shape, and a refusal that grew
    /// with the manifest would be a second way to make one request expensive.
    /// </summary>
    [Fact]
    public void A_manifest_of_many_offending_references_is_refused_by_one_sentence_for_each_rule()
    {
        var repeated = Enumerable.Repeat("att_alpha", 500).ToArray();

        ValidationResult result = Validator.Validate(WithAttachments([.. repeated, " "]));

        result.IsValid.ShouldBeFalse();
        result.Errors.Count.ShouldBe(2);
        result.Errors.Select(failure => failure.PropertyName).Distinct(StringComparer.Ordinal)
            .ShouldHaveSingleItem()
            .ShouldBe("Attachments");
    }

    private static RequestNotification.Command WithAttachments(IReadOnlyList<string> attachments)
        => new("araia-cambio", "cus_01J5X9", "critical", "auth.otp.login", 300)
        {
            Attachments = attachments,
        };

    private static RequestNotification.Command Bind(string body)
        => JsonSerializer.Deserialize<RequestNotification.Command>(body, Wire)
            ?? throw new InvalidOperationException("The body under test did not bind.");
}
