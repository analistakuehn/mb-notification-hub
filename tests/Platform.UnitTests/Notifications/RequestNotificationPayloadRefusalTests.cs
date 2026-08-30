using System.Text.Json;
using FluentValidation.Results;
using NotificationHub.Api.Modules.Notifications.Features.Ingress.RequestNotification;

namespace NotificationHub.UnitTests.Notifications;

/// <summary>
/// The ingestion door, over the two payload fields a producer controls. The
/// validator is the only point ahead of the catalog, the allowlist scan, the
/// idempotency hash and the bus settlement, so what it answers here is what
/// every transport answers.
/// </summary>
public sealed class RequestNotificationPayloadRefusalTests
{
    /// <summary>
    /// Raw text on purpose. Spelled as a C# escape the compiler would fold it
    /// into one code unit and the payload under test would never carry the six
    /// characters that make it unreadable.
    /// </summary>
    private const string LoneSurrogateEscape = @"\ud800";

    private static readonly RequestNotification.Validator Validator = new();

    [Fact]
    public void Variables_whose_escape_names_no_character_are_refused_without_throwing()
    {
        // The premise, asserted rather than assumed: the payload is legal JSON
        // text and binds without complaint. A payload that never parsed would
        // let the rest of the test pass while proving nothing.
        JsonElement variables = Parse($$"""{"orderId":"{{LoneSurrogateEscape}}"}""");
        variables.ValueKind.ShouldBe(JsonValueKind.Object);

        ValidationResult result = Should.NotThrow(() => Validator.Validate(Command(variables: variables)));

        result.IsValid.ShouldBeFalse();
        ValidationFailure failure = result.Errors.ShouldHaveSingleItem();
        failure.PropertyName.ShouldBe("Variables");

        // The refusal names the shape of the fault and never its size: a
        // producer told to shorten a payload that names no character has been
        // handed the wrong thing to fix.
        failure.ErrorMessage.ShouldBe(
            "Variables must be JSON text that can be read: an escape in it names no character.");
    }

    [Fact]
    public void Metadata_whose_escape_names_no_character_is_refused_without_throwing()
    {
        JsonElement metadata = Parse($$"""{"origin":"{{LoneSurrogateEscape}}"}""");
        metadata.ValueKind.ShouldBe(JsonValueKind.Object);

        ValidationResult result = Should.NotThrow(() => Validator.Validate(Command(metadata: metadata)));

        result.IsValid.ShouldBeFalse();
        ValidationFailure failure = result.Errors.ShouldHaveSingleItem();
        failure.PropertyName.ShouldBe("Metadata");
        failure.ErrorMessage.ShouldBe(
            "Metadata must be JSON text that can be read: an escape in it names no character.");
    }

    [Fact]
    public void An_ordinary_request_stays_valid_and_an_oversized_payload_is_still_refused_for_its_size()
    {
        // The falsifying pair. Without it the two refusals above would also be
        // produced by a validator that refused every request, and the door
        // would be closed on everything rather than on what cannot be read.
        Validator.Validate(Command()).IsValid.ShouldBeTrue();

        ValidationResult oversized = Validator.Validate(
            Command(variables: Parse($$"""{"v":"{{new string('x', 300_000)}}"}""")));

        oversized.IsValid.ShouldBeFalse();
        oversized.Errors.ShouldHaveSingleItem().ErrorMessage
            .ShouldBe("Variables must serialize to at most 262144 bytes of JSON.");
    }

    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static RequestNotification.Command Command(
        JsonElement? variables = null,
        JsonElement? metadata = null)
        => new("araia-cambio", "cus_01J5X9", "transactional", "template.key", 300)
        {
            Locale = "pt-BR",
            Variables = variables ?? Parse("""{"orderId":"ord-1"}"""),
            Metadata = metadata,
        };
}
