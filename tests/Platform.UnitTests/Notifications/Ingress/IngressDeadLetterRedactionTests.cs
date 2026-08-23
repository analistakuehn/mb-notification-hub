using System.Text.Json;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Consuming;

namespace NotificationHub.UnitTests.Notifications.Ingress;

/// <summary>
/// The redaction is the mitigation of a control that would otherwise defeat
/// itself: refusing a request for carrying a secret and then copying that
/// secret onto a topic with fourteen times the retention. These tests hold the
/// line that no value survives the copy.
/// </summary>
public sealed class IngressDeadLetterRedactionTests
{
    private const string Body = """
        {
          "specversion": "1.0",
          "id": "evt-1",
          "source": "urn:araia:kyc-service",
          "type": "araia.notification.requested.v1",
          "subject": "cus_01",
          "data": {
            "application": "araia-cambio",
            "recipientId": "cus_01",
            "idempotencyKey": "key-1",
            "class": "critical",
            "templateKey": "auth.otp",
            "variables": { "code": "483920", "expiresIn": "5" }
          }
        }
        """;

    [Fact]
    public void Redaction_replaces_the_variables_with_the_declared_names()
    {
        var redacted = IngressDeadLetterWriter.RedactVariables(Body, ["code"]);

        using JsonDocument document = JsonDocument.Parse(redacted);
        JsonElement variables = document.RootElement.GetProperty("data").GetProperty("variables");
        variables.ValueKind.ShouldBe(JsonValueKind.Array);
        variables.EnumerateArray().Select(item => item.GetString()).ShouldBe(["code"]);
    }

    [Fact]
    public void Redaction_carries_no_variable_value_anywhere_in_the_body()
    {
        var redacted = IngressDeadLetterWriter.RedactVariables(Body, ["code", "expiresIn"]);

        redacted.ShouldNotContain("483920");
        // Falsification: the untouched body does carry it, so the assertion
        // above is measuring the redaction and not the absence of the string.
        Body.ShouldContain("483920");
    }

    [Fact]
    public void Redaction_keeps_the_diagnostic_fields_the_producer_needs()
    {
        var redacted = IngressDeadLetterWriter.RedactVariables(Body, ["code"]);

        using JsonDocument document = JsonDocument.Parse(redacted);
        JsonElement data = document.RootElement.GetProperty("data");
        data.GetProperty("templateKey").GetString().ShouldBe("auth.otp");
        data.GetProperty("idempotencyKey").GetString().ShouldBe("key-1");
        document.RootElement.GetProperty("type").GetString().ShouldBe("araia.notification.requested.v1");
    }

    [Fact]
    public void An_unparseable_body_loses_everything_but_the_declared_names()
    {
        var redacted = IngressDeadLetterWriter.RedactVariables("{ not json at all", ["code"]);

        redacted.ShouldNotContain("not json at all");
        using JsonDocument document = JsonDocument.Parse(redacted);
        document.RootElement.GetProperty("redactedVariables")
            .EnumerateArray().Select(item => item.GetString()).ShouldBe(["code"]);
    }

    [Fact]
    public void A_body_without_a_data_section_loses_everything_but_the_declared_names()
    {
        var redacted = IngressDeadLetterWriter.RedactVariables("""{"specversion":"1.0","data":"483920"}""", ["code"]);

        redacted.ShouldNotContain("483920");
    }
}
