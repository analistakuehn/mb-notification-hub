using System.Text.Json;
using FluentValidation.Results;
using NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// The preview door. It shares the module's ceiling with the render that ships
/// a message, so a payload refused here is refused there and the two never
/// disagree about one payload.
/// </summary>
public sealed class RenderTemplateVersionValidatorTests
{
    /// <summary>
    /// Raw text on purpose. Spelled as a C# escape the compiler would fold it
    /// into one code unit and the payload under test would never carry the six
    /// characters that make it unreadable.
    /// </summary>
    private const string LoneSurrogateEscape = @"\ud800";

    private static readonly RenderTemplateVersion.Validator Validator = new();

    [Fact]
    public void Variables_whose_escape_names_no_character_are_refused_without_throwing()
    {
        // The premise, asserted rather than assumed: the payload is legal JSON
        // text and binds without complaint. A payload that never parsed would
        // let the rest of the test pass while proving nothing.
        JsonElement variables = Parse($$"""{"orderId":"{{LoneSurrogateEscape}}"}""");
        variables.ValueKind.ShouldBe(JsonValueKind.Object);

        ValidationResult result = Should.NotThrow(() => Validator.Validate(Request(variables)));

        result.IsValid.ShouldBeFalse();
        ValidationFailure failure = result.Errors.ShouldHaveSingleItem();
        failure.PropertyName.ShouldBe("Variables");

        // The refusal names the shape of the fault and never its size: a
        // caller told to shorten a payload that names no character has been
        // handed the wrong thing to fix.
        failure.ErrorMessage.ShouldBe(
            "Variables must be JSON text that can be read: an escape in it names no character.");
    }

    [Fact]
    public void An_ordinary_payload_stays_valid_and_an_oversized_one_is_still_refused_for_its_size()
    {
        // The falsifying pair. Without it the refusal above would also be
        // produced by a validator that refused every request.
        Validator.Validate(Request(Parse("""{"orderId":"ord-1"}"""))).IsValid.ShouldBeTrue();

        ValidationResult oversized = Validator.Validate(
            Request(Parse($$"""{"v":"{{new string('x', 300_000)}}"}""")));

        oversized.IsValid.ShouldBeFalse();
        oversized.Errors.ShouldHaveSingleItem().ErrorMessage
            .ShouldBe("Variables must serialize to at most 262144 bytes of JSON.");
    }

    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static RenderTemplateVersion.Request Request(JsonElement variables)
        => new("email", "pt-BR", variables);
}
