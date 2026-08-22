using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;

/// <summary>
/// Runs the sandbox analysis over every content field of a layout version,
/// producing the input the layout validation catalog consumes. Validate,
/// publish and rollback all share this path, so the validation a publisher
/// sees is exactly the one that gates the publication.
/// </summary>
internal sealed class LayoutVersionAnalyzer(ScribanTemplateEngine engine)
{
    internal List<ContentAnalysis> Analyze(LayoutVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Contents
            .Select(content => new ContentAnalysis(content.Channel, content.Locale, AnalyzeFields(content)))
            .ToList();
    }

    private List<ContentFieldAnalysis> AnalyzeFields(LayoutContent content)
    {
        List<ContentFieldAnalysis> fields = [Analyze(TemplateContentFields.Body, content.Body)];
        if (!string.IsNullOrEmpty(content.BodyText))
        {
            fields.Add(Analyze(TemplateContentFields.BodyText, content.BodyText));
        }

        return fields;
    }

    private ContentFieldAnalysis Analyze(string field, string source)
    {
        TemplateSourceAnalysis analysis = engine.Analyze(source, field);
        return new ContentFieldAnalysis(field, analysis.ParseSucceeded, analysis.ParseError, analysis.UsedVariables);
    }
}
