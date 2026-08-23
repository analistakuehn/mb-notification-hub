using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class VariablesSchemaTests
{
    [Fact]
    public void Reads_names_required_flags_url_formats_and_types_from_the_schema()
    {
        const string schema = """
            {
              "type": "object",
              "properties": {
                "code": { "type": "string" },
                "minutes": { "type": "integer" },
                "portalUrl": { "type": "string", "format": "url" },
                "docLink": { "format": "uri" }
              },
              "required": ["code"]
            }
            """;

        var parsed = VariablesSchema.TryParse(schema, out IReadOnlyList<VariableDeclaration> declarations);

        parsed.ShouldBeTrue();
        declarations.ShouldBe([
            new VariableDeclaration("code", Required: true, IsUrl: false) { Type = "string" },
            new VariableDeclaration("minutes", Required: false, IsUrl: false) { Type = "integer" },
            new VariableDeclaration("portalUrl", Required: false, IsUrl: true) { Type = "string" },
            new VariableDeclaration("docLink", Required: false, IsUrl: true),
        ]);
    }

    [Fact]
    public void A_missing_schema_yields_no_declarations()
    {
        var parsed = VariablesSchema.TryParse(null, out IReadOnlyList<VariableDeclaration> declarations);

        parsed.ShouldBeTrue();
        declarations.ShouldBeEmpty();
    }

    [Fact]
    public void Broken_json_is_reported_instead_of_throwing()
    {
        var parsed = VariablesSchema.TryParse("{ not json", out IReadOnlyList<VariableDeclaration> declarations);

        parsed.ShouldBeFalse();
        declarations.ShouldBeEmpty();
    }

    [Fact]
    public void A_schema_without_properties_yields_no_declarations()
    {
        var parsed = VariablesSchema.TryParse("""{ "type": "object" }""", out IReadOnlyList<VariableDeclaration> declarations);

        parsed.ShouldBeTrue();
        declarations.ShouldBeEmpty();
    }
}
