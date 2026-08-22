using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;

/// <summary>
/// Runs the sandbox analysis over every content field of a version, producing
/// the input the integral validation catalog consumes. Validate, publish and
/// rollback all share this path, so the validation a publisher sees is exactly
/// the one that gates the publication.
/// </summary>
internal sealed class TemplateVersionAnalyzer(ScribanTemplateEngine engine)
{
    internal List<ContentAnalysis> Analyze(TemplateVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Contents
            .Select(content => new ContentAnalysis(content.Channel, content.Locale, AnalyzeFields(content)))
            .ToList();
    }

    private List<ContentFieldAnalysis> AnalyzeFields(TemplateContent content)
    {
        List<ContentFieldAnalysis> fields = [];
        if (!string.IsNullOrEmpty(content.Subject))
        {
            fields.Add(Analyze(TemplateContentFields.Subject, content.Subject));
        }

        fields.Add(Analyze(TemplateContentFields.Body, content.Body));
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
