using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class VariablesSchemaTests
{
    [Fact]
    public void Reads_names_required_flags_and_url_formats_from_the_schema()
    {
        const string schema = """
            {
              "type": "object",
              "properties": {
                "code": { "type": "string" },
                "portalUrl": { "type": "string", "format": "url" },
                "docLink": { "type": "string", "format": "uri" }
              },
              "required": ["code"]
            }
            """;

        bool parsed = VariablesSchema.TryParse(schema, out IReadOnlyList<VariableDeclaration> declarations);

        parsed.ShouldBeTrue();
        declarations.ShouldBe([
            new VariableDeclaration("code", Required: true, IsUrl: false),
            new VariableDeclaration("portalUrl", Required: false, IsUrl: true),
            new VariableDeclaration("docLink", Required: false, IsUrl: true),
        ]);
    }

    [Fact]
    public void A_missing_schema_yields_no_declarations()
    {
        bool parsed = VariablesSchema.TryParse(null, out IReadOnlyList<VariableDeclaration> declarations);

        parsed.ShouldBeTrue();
        declarations.ShouldBeEmpty();
    }

    [Fact]
    public void Broken_json_is_reported_instead_of_throwing()
    {
        bool parsed = VariablesSchema.TryParse("{ not json", out IReadOnlyList<VariableDeclaration> declarations);

        parsed.ShouldBeFalse();
        declarations.ShouldBeEmpty();
    }

    [Fact]
    public void A_schema_without_properties_yields_no_declarations()
    {
        bool parsed = VariablesSchema.TryParse("""{ "type": "object" }""", out IReadOnlyList<VariableDeclaration> declarations);

        parsed.ShouldBeTrue();
        declarations.ShouldBeEmpty();
    }
}
