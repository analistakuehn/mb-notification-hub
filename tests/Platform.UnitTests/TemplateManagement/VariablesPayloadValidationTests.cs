using System.Text.Json;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class VariablesPayloadValidationTests
{
    private const string Schema = """
        {
          "type": "object",
          "properties": {
            "orderId": { "type": "string" },
            "minutes": { "type": "integer" },
            "portalUrl": { "type": "string", "format": "url" }
          },
          "required": ["orderId"]
        }
        """;

    [Fact]
    public void A_clean_payload_produces_a_fully_passed_report()
    {
        ValidationReport report = VariablesPayloadValidation.Validate(
            MakeTemplate(),
            Schema,
            Variables("""{ "orderId": "42", "minutes": 5, "portalUrl": "https://app.montebravo.com.br/x" }"""));

        report.Passed.ShouldBeTrue();
        report.Checks.Select(check => check.Name).ShouldBe([
            ValidationCheckNames.VariablesSchema,
            ValidationCheckNames.VariablesDeclared,
            ValidationCheckNames.VariablesRequired,
            ValidationCheckNames.VariablesTypes,
            ValidationCheckNames.UrlAllowlist,
        ]);
        report.Checks.ShouldAllBe(check => check.Status == ValidationCheckStatuses.Passed);
    }

    [Fact]
    public void A_provided_variable_the_schema_does_not_declare_fails()
    {
        ValidationReport report = VariablesPayloadValidation.Validate(
            MakeTemplate(),
            Schema,
            Variables("""{ "orderId": "42", "cupom": "MB10" }"""));

        ValidationCheck check = report.Checks.Single(candidate =>
            candidate.Name == ValidationCheckNames.VariablesDeclared);
        check.Status.ShouldBe(ValidationCheckStatuses.Failed);
        check.Message.ShouldContain("'cupom'");
        check.Message.ShouldNotContain("MB10");
        report.Passed.ShouldBeFalse();
    }

    [Fact]
    public void A_missing_required_variable_fails()
    {
        ValidationReport report = VariablesPayloadValidation.Validate(
            MakeTemplate(),
            Schema,
            Variables("""{ "minutes": 5 }"""));

        ValidationCheck check = report.Checks.Single(candidate =>
            candidate.Name == ValidationCheckNames.VariablesRequired);
        check.Status.ShouldBe(ValidationCheckStatuses.Failed);
        check.Message.ShouldContain("'orderId'");
    }

    [Fact]
    public void An_absent_payload_fails_only_the_required_declarations()
    {
        ValidationReport report = VariablesPayloadValidation.Validate(MakeTemplate(), Schema, null);

        report.Checks.Single(candidate => candidate.Name == ValidationCheckNames.VariablesRequired)
            .Status.ShouldBe(ValidationCheckStatuses.Failed);
        report.Checks.Single(candidate => candidate.Name == ValidationCheckNames.VariablesDeclared)
            .Status.ShouldBe(ValidationCheckStatuses.Passed);
        report.Checks.Single(candidate => candidate.Name == ValidationCheckNames.VariablesTypes)
            .Status.ShouldBe(ValidationCheckStatuses.Passed);
    }

    [Fact]
    public void A_value_incompatible_with_its_declared_type_fails()
    {
        ValidationReport report = VariablesPayloadValidation.Validate(
            MakeTemplate(),
            Schema,
            Variables("""{ "orderId": "42", "minutes": "cinco" }"""));

        ValidationCheck check = report.Checks.Single(candidate =>
            candidate.Name == ValidationCheckNames.VariablesTypes);
        check.Status.ShouldBe(ValidationCheckStatuses.Failed);
        check.Message.ShouldContain("'minutes'");
        check.Message.ShouldContain("'integer'");
    }

    [Fact]
    public void A_url_variable_outside_the_allowlist_fails_without_leaking_the_value()
    {
        ValidationReport report = VariablesPayloadValidation.Validate(
            MakeTemplate(),
            Schema,
            Variables("""{ "orderId": "42", "portalUrl": "https://phishing.example.io/login" }"""));

        ValidationCheck check = report.Checks.Single(candidate =>
            candidate.Name == ValidationCheckNames.UrlAllowlist);
        check.Status.ShouldBe(ValidationCheckStatuses.Failed);
        check.Message.ShouldContain("'portalUrl'");
        check.Message.ShouldNotContain("phishing.example.io");
    }

    [Fact]
    public void A_string_variable_carrying_a_host_outside_the_allowlist_fails_without_leaking_the_value()
    {
        ValidationReport report = VariablesPayloadValidation.Validate(
            MakeTemplate(),
            """{ "type": "object", "properties": { "link": { "type": "string" } } }""",
            Variables("""{ "link": "https://evil.example.io/pay?token=abc" }"""));

        ValidationCheck check = report.Checks.Single(candidate =>
            candidate.Name == ValidationCheckNames.UrlAllowlist);
        check.Status.ShouldBe(ValidationCheckStatuses.Failed);
        check.Message.ShouldContain("evil.example.io");
        check.Message.ShouldNotContain("token");
        report.Passed.ShouldBeFalse();
    }

    [Fact]
    public void A_host_inside_an_array_element_of_the_payload_fails()
    {
        ValidationReport report = VariablesPayloadValidation.Validate(
            MakeTemplate(),
            """{ "type": "object", "properties": { "items": { "type": "array" } } }""",
            Variables("""{ "items": [ { "detail": "veja em //evil.example.io/promo" } ] }"""));

        ValidationCheck check = report.Checks.Single(candidate =>
            candidate.Name == ValidationCheckNames.UrlAllowlist);
        check.Status.ShouldBe(ValidationCheckStatuses.Failed);
        check.Message.ShouldContain("evil.example.io");
    }

    [Fact]
    public void A_string_variable_carrying_a_host_inside_the_allowlist_passes()
    {
        ValidationReport report = VariablesPayloadValidation.Validate(
            MakeTemplate(),
            """{ "type": "object", "properties": { "link": { "type": "string" } } }""",
            Variables("""{ "link": "https://app.montebravo.com.br/pedidos" }"""));

        report.Checks.Single(candidate => candidate.Name == ValidationCheckNames.UrlAllowlist)
            .Status.ShouldBe(ValidationCheckStatuses.Passed);
    }

    [Fact]
    public void A_document_number_in_a_string_variable_passes()
    {
        ValidationReport report = VariablesPayloadValidation.Validate(
            MakeTemplate(),
            """{ "type": "object", "properties": { "document": { "type": "string" } } }""",
            Variables("""{ "document": "CNPJ 12.345.678/0001-90" }"""));

        report.Checks.Single(candidate => candidate.Name == ValidationCheckNames.UrlAllowlist)
            .Status.ShouldBe(ValidationCheckStatuses.Passed);
    }

    [Fact]
    public void A_payload_that_is_not_a_json_object_fails_the_declaration_check()
    {
        ValidationReport report = VariablesPayloadValidation.Validate(
            MakeTemplate(),
            Schema,
            Variables("""["orderId"]"""));

        ValidationCheck check = report.Checks.Single(candidate =>
            candidate.Name == ValidationCheckNames.VariablesDeclared);
        check.Status.ShouldBe(ValidationCheckStatuses.Failed);
        check.Message.ShouldContain("JSON object");
    }

    [Fact]
    public void An_unreadable_schema_fails_its_check_and_skips_the_declaration_checks()
    {
        ValidationReport report = VariablesPayloadValidation.Validate(
            MakeTemplate(),
            "{ not json",
            Variables("""{ "orderId": "42" }"""));

        report.Checks.Single(candidate => candidate.Name == ValidationCheckNames.VariablesSchema)
            .Status.ShouldBe(ValidationCheckStatuses.Failed);
        report.Checks.ShouldNotContain(candidate => candidate.Name == ValidationCheckNames.VariablesDeclared);
        report.Passed.ShouldBeFalse();
    }

    [Fact]
    public void A_version_without_schema_rejects_any_provided_variable_as_undeclared()
    {
        ValidationReport report = VariablesPayloadValidation.Validate(
            MakeTemplate(),
            null,
            Variables("""{ "orderId": "42" }"""));

        ValidationCheck check = report.Checks.Single(candidate =>
            candidate.Name == ValidationCheckNames.VariablesDeclared);
        check.Status.ShouldBe(ValidationCheckStatuses.Failed);
        check.Message.ShouldContain("'orderId'");
    }

    private static Template MakeTemplate()
        => Template.Create(TemplateKey.Create("orders.status.changed").Value!, new TemplateMetadata
        {
            Application = "araia-cambio",
            Class = NotificationClass.Transactional,
            OwnerTeam = "growth-squad",
            Purpose = "order-updates",
            LegalBasis = "execucao-de-contrato",
            DefaultLocale = Locale.Create("pt-BR").Value,
            LinkDomainsAllowed = ["montebravo.com.br"],
        }).Value!;

    private static JsonElement Variables(string json)
        => JsonSerializer.Deserialize<JsonElement>(json);
}
