using System.Text.Json;
using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.UnitTests.Notifications;

public sealed class VariablesMaskTests
{
    private static JsonElement Parse(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Only_the_sensitive_variables_are_masked()
    {
        var masked = VariablesMask.MaskedProjection(
            Parse("""{"code":"482913","expiresInMinutes":5}"""),
            ["code"]);

        masked.ShouldBe("""{"code":"***","expiresInMinutes":5}""");
    }

    [Fact]
    public void A_sensitive_container_keeps_its_shape_with_every_leaf_masked()
    {
        var masked = VariablesMask.MaskedProjection(
            Parse("""{"secret":{"token":"abc","codes":[1,2]},"plain":"x"}"""),
            ["secret"]);

        masked.ShouldBe("""{"plain":"x","secret":{"codes":["***","***"],"token":"***"}}""");
    }

    [Fact]
    public void A_sensitive_null_stays_null()
    {
        var masked = VariablesMask.MaskedProjection(
            Parse("""{"code":null,"other":1}"""),
            ["code"]);

        masked.ShouldBe("""{"code":null,"other":1}""");
    }

    [Fact]
    public void A_sensitive_name_absent_from_the_payload_changes_nothing()
    {
        var masked = VariablesMask.MaskedProjection(
            Parse("""{"orderId":"ord-1"}"""),
            ["code"]);

        masked.ShouldBe("""{"orderId":"ord-1"}""");
    }

    [Fact]
    public void An_absent_payload_projects_to_an_empty_object()
    {
        VariablesMask.MaskedProjection(null, ["code"]).ShouldBe("{}");
        VariablesMask.MaskedProjection(Parse("null"), ["code"]).ShouldBe("{}");
    }

    [Fact]
    public void The_projection_is_canonical_with_keys_in_ordinal_order()
    {
        var masked = VariablesMask.MaskedProjection(
            Parse("""{ "b": 2, "a": 1 }"""),
            []);

        masked.ShouldBe("""{"a":1,"b":2}""");
    }
}
