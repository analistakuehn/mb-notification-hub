using System.Text.Json;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.SharedKernel;

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

    [Theory]
    [MemberData(nameof(SensitiveMaskingCases.All), MemberType = typeof(SensitiveMaskingCases))]
    public void Every_shared_masking_case_hides_the_planted_value(string scenario)
        => AssertCase(SensitiveMaskingCases.For(scenario));

    [Fact]
    public void A_sensitive_value_addressed_by_a_nested_property_is_masked()
        => AssertCase(SensitiveMaskingCases.For(SensitiveMaskingCases.NestedProperty));

    [Fact]
    public void A_sensitive_value_inside_an_array_element_is_masked()
        => AssertCase(SensitiveMaskingCases.For(SensitiveMaskingCases.ArrayElement));

    [Fact]
    public void A_sibling_of_a_nested_sensitive_value_survives_the_mask()
        => AssertCase(SensitiveMaskingCases.For(SensitiveMaskingCases.NestedSibling));

    [Fact]
    public void A_sensitive_name_that_collides_under_another_container_is_narrowed_by_the_exact_path()
        => AssertCase(SensitiveMaskingCases.For(SensitiveMaskingCases.ExactPathNarrowing));

    [Fact]
    public void A_sensitive_path_resolving_to_null_keeps_the_null()
        => AssertCase(SensitiveMaskingCases.For(SensitiveMaskingCases.PathToNull));

    [Fact]
    public void A_top_level_key_that_spells_the_sensitive_path_literally_is_not_taken_for_the_path()
        => AssertCase(SensitiveMaskingCases.For(SensitiveMaskingCases.LiteralPathKey));

    [Fact]
    public void A_payload_carrying_a_duplicated_key_does_not_break_the_mask()
    {
        SensitiveMaskingCase testCase = SensitiveMaskingCases.For(SensitiveMaskingCases.DuplicatedKey);

        JsonElement masked = Should.NotThrow(() => VariableMasking
            .MaskSensitiveVariables(Variables(testCase.Payload), testCase.SensitiveNames)!.Value);

        SensitiveMaskingCases.AssertMaskedForm(masked.GetRawText(), masked, testCase);
    }

    [Fact]
    public void A_payload_whose_sensitive_path_breaks_on_a_non_object_prefix_is_refused()
    {
        SensitiveMaskingCase testCase = SensitiveMaskingCases.For(SensitiveMaskingCases.BrokenPrefix);

        AssertCase(testCase);

        SensitiveValueMask.Outcome outcome = VariableMasking.Mask(
            Variables(testCase.Payload), testCase.SensitiveNames);
        outcome.RefusedName.ShouldBe(testCase.RefusedName);
        outcome.IsRefused.ShouldBeTrue();
    }

    [Fact]
    public void A_payload_with_no_sensitive_value_still_reuses_the_complete_form()
    {
        SensitiveMaskingCase testCase = SensitiveMaskingCases.For(SensitiveMaskingCases.NothingToMask);
        JsonElement? payload = Variables(testCase.Payload);

        JsonElement? masked = VariableMasking.MaskSensitiveVariables(payload, testCase.SensitiveNames);

        masked.ShouldBe(payload);
        VariableMasking.RequiresMasking(payload, testCase.SensitiveNames).ShouldBeFalse();
    }

    private static void AssertCase(SensitiveMaskingCase testCase)
    {
        JsonElement masked = VariableMasking
            .MaskSensitiveVariables(Variables(testCase.Payload), testCase.SensitiveNames)!.Value;

        SensitiveMaskingCases.AssertMaskedForm(masked.GetRawText(), masked, testCase);
    }

    private static JsonElement Variables(string json)
        => JsonSerializer.Deserialize<JsonElement>(json);
}
