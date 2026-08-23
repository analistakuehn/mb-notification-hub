using System.Text.Json;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class VariableMaskingTests
{
    [Fact]
    public void A_sensitive_scalar_value_becomes_the_fixed_mask()
    {
        JsonElement masked = VariableMasking.MaskSensitiveVariables(
            Variables("""{ "code": "998877", "orderId": "42" }"""),
            ["code"])!.Value;

        masked.GetProperty("code").GetString().ShouldBe(VariableMasking.MaskedValue);
        masked.GetProperty("orderId").GetString().ShouldBe("42");
    }

    [Fact]
    public void A_sensitive_number_is_masked_as_the_same_fixed_mask()
    {
        JsonElement masked = VariableMasking.MaskSensitiveVariables(
            Variables("""{ "balance": 1234.56 }"""),
            ["balance"])!.Value;

        masked.GetProperty("balance").GetString().ShouldBe(VariableMasking.MaskedValue);
    }

    [Fact]
    public void A_sensitive_container_keeps_its_shape_with_every_leaf_masked()
    {
        JsonElement masked = VariableMasking.MaskSensitiveVariables(
            Variables("""{ "account": { "number": "12345-6", "holders": ["Ana", "Rui"], "note": null } }"""),
            ["account"])!.Value;

        JsonElement account = masked.GetProperty("account");
        account.GetProperty("number").GetString().ShouldBe(VariableMasking.MaskedValue);
        account.GetProperty("holders").EnumerateArray()
            .Select(item => item.GetString())
            .ShouldBe([VariableMasking.MaskedValue, VariableMasking.MaskedValue]);
        account.GetProperty("note").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void A_payload_without_the_sensitive_variable_comes_back_unchanged()
    {
        JsonElement? payload = Variables("""{ "orderId": "42" }""");

        JsonElement? masked = VariableMasking.MaskSensitiveVariables(payload, ["code"]);

        masked.ShouldBe(payload);
        VariableMasking.RequiresMasking(payload, ["code"]).ShouldBeFalse();
    }

    [Fact]
    public void An_absent_payload_needs_no_masking()
    {
        VariableMasking.MaskSensitiveVariables(null, ["code"]).ShouldBeNull();
        VariableMasking.RequiresMasking(null, ["code"]).ShouldBeFalse();
    }

    [Fact]
    public void A_payload_carrying_a_sensitive_variable_requires_masking()
    {
        VariableMasking.RequiresMasking(Variables("""{ "code": "998877" }"""), ["code"]).ShouldBeTrue();
    }

    [Fact]
    public void Masking_changes_the_canonical_hash_of_the_rendered_fields()
    {
        var fullHash = CanonicalHash.OfFields("Acesso", "Código 998877", null);
        var maskedHash = CanonicalHash.OfFields("Acesso", $"Código {VariableMasking.MaskedValue}", null);
        var repeatedFullHash = CanonicalHash.OfFields("Acesso", "Código 998877", null);

        maskedHash.ShouldNotBe(fullHash);
        repeatedFullHash.ShouldBe(fullHash);
    }

    private static JsonElement Variables(string json)
        => JsonSerializer.Deserialize<JsonElement>(json);
}
