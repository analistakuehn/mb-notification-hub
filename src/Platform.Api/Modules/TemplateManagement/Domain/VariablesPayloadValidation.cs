using System.Text.Json;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// Validates a producer variables payload against the variables schema of a
/// version, mirroring the integral catalog semantics: a provided variable the
/// schema does not declare fails, every required declaration must be provided,
/// declared types must match the provided values, and URL variables must be
/// absolute http(s) URLs inside the template's allowed domains. The report is
/// a value, never an error: running the validation succeeds even when checks
/// fail, and variable values never travel in a message.
/// </summary>
public static class VariablesPayloadValidation
{
    public static ValidationReport Validate(Template template, string? variablesSchemaJson, JsonElement? variables)
    {
        ArgumentNullException.ThrowIfNull(template);

        List<ValidationCheck> checks = [];
        var schemaParsed = VariablesSchema.TryParse(
            variablesSchemaJson,
            out IReadOnlyList<VariableDeclaration> declarations);
        if (variablesSchemaJson is not null)
        {
            checks.Add(schemaParsed
                ? Passed(ValidationCheckNames.VariablesSchema, "The variables schema is readable.")
                : Failed(ValidationCheckNames.VariablesSchema, "The variables schema is not JSON this system can read."));
        }

        JsonElement? payload = variables is { ValueKind: JsonValueKind.Object } provided ? provided : null;
        if (schemaParsed)
        {
            AddDeclaredChecks(checks, declarations, variables, payload);
            AddRequiredChecks(checks, declarations, payload);
            AddTypeChecks(checks, declarations, payload);
        }

        AddUrlChecks(checks, template, declarations, payload);
        return new ValidationReport(checks);
    }

    private static void AddDeclaredChecks(
        List<ValidationCheck> checks,
        IReadOnlyList<VariableDeclaration> declarations,
        JsonElement? variables,
        JsonElement? payload)
    {
        if (variables is { } present && payload is null
            && present.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            checks.Add(Failed(ValidationCheckNames.VariablesDeclared, "The variables payload must be a JSON object."));
            return;
        }

        HashSet<string> declared = new(declarations.Select(declaration => declaration.Name), StringComparer.Ordinal);
        var before = checks.Count;
        if (payload is { } provided)
        {
            foreach (JsonProperty property in provided.EnumerateObject())
            {
                if (!declared.Contains(property.Name))
                {
                    checks.Add(Failed(
                        ValidationCheckNames.VariablesDeclared,
                        $"Variable '{property.Name}' is provided but not declared in the variables schema."));
                }
            }
        }

        if (checks.Count == before)
        {
            checks.Add(Passed(
                ValidationCheckNames.VariablesDeclared,
                "Every provided variable is declared in the variables schema."));
        }
    }

    private static void AddRequiredChecks(
        List<ValidationCheck> checks,
        IReadOnlyList<VariableDeclaration> declarations,
        JsonElement? payload)
    {
        var before = checks.Count;
        foreach (VariableDeclaration declaration in declarations.Where(declaration => declaration.Required))
        {
            if (payload is not { } provided || !provided.TryGetProperty(declaration.Name, out _))
            {
                checks.Add(Failed(
                    ValidationCheckNames.VariablesRequired,
                    $"Variable '{declaration.Name}' is required but was not provided."));
            }
        }

        if (checks.Count == before)
        {
            checks.Add(Passed(ValidationCheckNames.VariablesRequired, "Every required variable is provided."));
        }
    }

    private static void AddTypeChecks(
        List<ValidationCheck> checks,
        IReadOnlyList<VariableDeclaration> declarations,
        JsonElement? payload)
    {
        var before = checks.Count;
        if (payload is { } provided)
        {
            foreach (VariableDeclaration declaration in declarations.Where(declaration => declaration.Type is not null))
            {
                if (provided.TryGetProperty(declaration.Name, out JsonElement value)
                    && !MatchesType(declaration.Type!, value))
                {
                    checks.Add(Failed(
                        ValidationCheckNames.VariablesTypes,
                        $"Variable '{declaration.Name}' must be of type '{declaration.Type}'."));
                }
            }
        }

        if (checks.Count == before)
        {
            checks.Add(Passed(
                ValidationCheckNames.VariablesTypes,
                "Every provided variable matches its declared type."));
        }
    }

    private static void AddUrlChecks(
        List<ValidationCheck> checks,
        Template template,
        IReadOnlyList<VariableDeclaration> declarations,
        JsonElement? payload)
    {
        var before = checks.Count;
        if (payload is { } provided)
        {
            foreach (VariableDeclaration declaration in declarations.Where(declaration => declaration.IsUrl))
            {
                // The value never travels in the message: it may embed tokens
                // or personal data in the query string.
                if (provided.TryGetProperty(declaration.Name, out JsonElement value)
                    && !LinkDomainPolicy.IsAllowedUrlValue(template, value))
                {
                    checks.Add(Failed(
                        ValidationCheckNames.UrlAllowlist,
                        $"Variable '{declaration.Name}' must be an absolute http(s) URL "
                        + "inside the template's allowed domains."));
                }
            }
        }

        if (checks.Count == before)
        {
            // Only the host travels in the finding, never the value: the query
            // string may carry a token or personal data. Same reason as the
            // loop above. A declared URL variable already refused says the
            // same thing about the same payload, so the scan answers only when
            // that loop found nothing.
            var offending = LinkDomainPolicy.FirstDisallowedHost(payload, template);
            if (offending is not null)
            {
                checks.Add(Failed(
                    ValidationCheckNames.UrlAllowlist,
                    $"A variable value carries link host '{offending}', "
                    + "which is outside the template's allowed domains."));
            }
        }

        if (checks.Count == before)
        {
            checks.Add(Passed(ValidationCheckNames.UrlAllowlist, "URL variables respect the allowed domains."));
        }
    }

    private static bool MatchesType(string type, JsonElement value) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "null" => value.ValueKind == JsonValueKind.Null,

        // A type this vocabulary does not know belongs to a newer writer, never to an error.
        _ => true,
    };

    private static ValidationCheck Passed(string name, string message)
        => new(name, ValidationCheckStatuses.Passed, message, null);

    private static ValidationCheck Failed(string name, string message)
        => new(name, ValidationCheckStatuses.Failed, message, null);
}
