using System.Text.Json;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.SharedKernel;
using NotificationHub.UnitTests.TemplateManagement;

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

        var projection = Should.NotThrow(() => VariablesMask
            .MaskedProjection(Parse(testCase.Payload), testCase.SensitiveNames));

        using var parsed = JsonDocument.Parse(projection);
        SensitiveMaskingCases.AssertMaskedForm(projection, parsed.RootElement, testCase);
    }

    [Fact]
    public void A_payload_whose_sensitive_path_breaks_on_a_non_object_prefix_is_refused()
    {
        SensitiveMaskingCase testCase = SensitiveMaskingCases.For(SensitiveMaskingCases.BrokenPrefix);

        AssertCase(testCase);

        SensitiveValueMask.Outcome outcome = VariablesMask.Mask(
            Parse(testCase.Payload), testCase.SensitiveNames);
        outcome.RefusedName.ShouldBe(testCase.RefusedName);
        outcome.IsRefused.ShouldBeTrue();
    }

    [Fact]
    public void A_payload_with_no_sensitive_value_still_reuses_the_complete_form()
    {
        SensitiveMaskingCase testCase = SensitiveMaskingCases.For(SensitiveMaskingCases.NothingToMask);
        JsonElement payload = Parse(testCase.Payload);

        var masked = VariablesMask.MaskedProjection(payload, testCase.SensitiveNames);

        masked.ShouldBe(VariablesMask.MaskedProjection(payload, []));
    }

    private static void AssertCase(SensitiveMaskingCase testCase)
    {
        var projection = VariablesMask.MaskedProjection(Parse(testCase.Payload), testCase.SensitiveNames);

        using var parsed = JsonDocument.Parse(projection);
        SensitiveMaskingCases.AssertMaskedForm(projection, parsed.RootElement, testCase);
    }
}
