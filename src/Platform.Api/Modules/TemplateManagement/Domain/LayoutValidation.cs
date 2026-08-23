using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// The layout validation catalog. Every check runs and lands in the report;
/// a failed check never interrupts the run, because the full <c>checks[]</c>
/// list is the value the caller is after and any failure blocks publication.
/// </summary>
public static class LayoutValidation
{
    /// <summary>
    /// Name of the variable a layout body must read: the rendered template
    /// content lands exactly where the layout places it.
    /// </summary>
    public const string ContentPlaceholderVariable = "content";

    public static ValidationReport Validate(LayoutVersion version, IReadOnlyList<ContentAnalysis> analyses)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(analyses);

        List<ValidationCheck> checks = [];
        AddCompilationChecks(checks, analyses);
        AddContentPlaceholderChecks(checks, version, analyses);
        return new ValidationReport(checks);
    }

    private static void AddCompilationChecks(List<ValidationCheck> checks, IReadOnlyList<ContentAnalysis> analyses)
    {
        var before = checks.Count;
        foreach (ContentAnalysis analysis in analyses)
        {
            foreach (ContentFieldAnalysis field in analysis.Fields)
            {
                if (!field.ParseSucceeded)
                {
                    checks.Add(Failed(
                        ValidationCheckNames.Compilation,
                        field.ParseError ?? "The layout failed to compile.",
                        At(analysis.Channel, analysis.Locale, field.Field)));
                }
            }
        }

        if (checks.Count == before)
        {
            checks.Add(Passed(ValidationCheckNames.Compilation, "All content compiled inside the sandbox limits."));
        }
    }

    private static void AddContentPlaceholderChecks(
        List<ValidationCheck> checks,
        LayoutVersion version,
        IReadOnlyList<ContentAnalysis> analyses)
    {
        if (version.Contents.Count == 0)
        {
            checks.Add(Failed(
                ValidationCheckNames.ContentPlaceholder,
                "The layout version has no content to validate.",
                null));
            return;
        }

        var before = checks.Count;
        foreach (ContentAnalysis analysis in analyses)
        {
            foreach (ContentFieldAnalysis field in analysis.Fields)
            {
                if (field.ParseSucceeded
                    && !field.UsedVariables.Contains(ContentPlaceholderVariable, StringComparer.Ordinal))
                {
                    checks.Add(Failed(
                        ValidationCheckNames.ContentPlaceholder,
                        $"The layout must read the '{ContentPlaceholderVariable}' variable: "
                        + "it marks where the rendered template body lands.",
                        At(analysis.Channel, analysis.Locale, field.Field)));
                }
            }
        }

        if (checks.Count == before)
        {
            checks.Add(Passed(
                ValidationCheckNames.ContentPlaceholder,
                $"Every content field reads the '{ContentPlaceholderVariable}' placeholder."));
        }
    }

    private static string At(Channel channel, Locale locale, string field)
        => $"{channel.Value}/{locale.Value}/{field}";

    private static ValidationCheck Passed(string name, string message)
        => new(name, ValidationCheckStatuses.Passed, message, null);

    private static ValidationCheck Failed(string name, string message, string? location)
        => new(name, ValidationCheckStatuses.Failed, message, location);
}
