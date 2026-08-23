using System.Text.RegularExpressions;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>Canonical field names of a content entry, used in check locations.</summary>
public static class TemplateContentFields
{
    public const string Subject = "subject";
    public const string Body = "body";
    public const string BodyText = "bodyText";
}

/// <summary>Canonical names of the checks the integral validation runs.</summary>
public static class ValidationCheckNames
{
    public const string Compilation = "compilation";
    public const string VariablesSchema = "variables-schema";
    public const string VariablesDeclared = "variables-declared";
    public const string VariablesUsed = "variables-used";
    public const string UrlAllowlist = "url-allowlist";
    public const string SensitiveVariables = "sensitive-variables";
    public const string ChannelLimits = "channel-limits";
    public const string DefaultLocale = "default-locale";
    public const string LayoutReference = "layout-reference";
    public const string ContentPlaceholder = "content-placeholder";
}

/// <summary>Sandbox analysis of one field of a content entry.</summary>
public sealed record ContentFieldAnalysis(
    string Field,
    bool ParseSucceeded,
    string? ParseError,
    IReadOnlyList<string> UsedVariables);

/// <summary>Sandbox analysis of one (channel, locale) content entry.</summary>
public sealed record ContentAnalysis(
    Channel Channel,
    Locale Locale,
    IReadOnlyList<ContentFieldAnalysis> Fields);

/// <summary>
/// The integral validation catalog. Every check runs and lands in the report;
/// a failed check never interrupts the run, because the full <c>checks[]</c>
/// list is the value the caller is after and any failure blocks publication.
/// </summary>
public static partial class TemplateValidation
{
    public const int SmsMaxBodyChars = 1600;
    public const int PushMaxSubjectChars = 200;
    public const int PushMaxBodyChars = 4000;

    public static ValidationReport Validate(
        Template template,
        TemplateVersion version,
        IReadOnlyList<ContentAnalysis> analyses,
        LayoutReferenceFacts? layoutReference = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(analyses);
        if (version.LayoutKey is not null && layoutReference is null)
        {
            throw new ArgumentException(
                "The version pins a layout; the caller must supply the layout-reference facts.",
                nameof(layoutReference));
        }

        List<ValidationCheck> checks = [];
        AddCompilationChecks(checks, analyses);
        var schemaParsed = VariablesSchema.TryParse(
            version.VariablesSchemaJson,
            out IReadOnlyList<VariableDeclaration> declarations);
        AddSchemaChecks(checks, version.VariablesSchemaJson, schemaParsed);
        if (schemaParsed)
        {
            AddDeclaredVariableChecks(checks, declarations, analyses);
            AddUnusedVariableChecks(checks, declarations, analyses);
        }

        AddUrlChecks(checks, template, version, declarations, analyses);
        AddSensitiveVariableChecks(checks, template, version);
        AddChannelLimitChecks(checks, version);
        AddDefaultLocaleChecks(checks, template, version);
        AddLayoutReferenceChecks(checks, version, layoutReference);
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
                        field.ParseError ?? "The template failed to compile.",
                        At(analysis.Channel, analysis.Locale, field.Field)));
                }
            }
        }

        if (checks.Count == before)
        {
            checks.Add(Passed(ValidationCheckNames.Compilation, "All content compiled inside the sandbox limits."));
        }
    }

    private static void AddSchemaChecks(List<ValidationCheck> checks, string? schemaJson, bool schemaParsed)
    {
        if (schemaJson is null)
        {
            return;
        }

        checks.Add(schemaParsed
            ? Passed(ValidationCheckNames.VariablesSchema, "The variables schema is readable.")
            : Failed(ValidationCheckNames.VariablesSchema, "The variables schema is not valid JSON.", null));
    }

    private static void AddDeclaredVariableChecks(
        List<ValidationCheck> checks,
        IReadOnlyList<VariableDeclaration> declarations,
        IReadOnlyList<ContentAnalysis> analyses)
    {
        HashSet<string> declared = new(declarations.Select(declaration => declaration.Name), StringComparer.Ordinal);
        var before = checks.Count;
        foreach (ContentAnalysis analysis in analyses)
        {
            foreach (ContentFieldAnalysis field in analysis.Fields)
            {
                foreach (var variable in field.UsedVariables.Where(variable => !declared.Contains(variable)))
                {
                    checks.Add(Failed(
                        ValidationCheckNames.VariablesDeclared,
                        $"Variable '{variable}' is used but not declared in the variables schema.",
                        At(analysis.Channel, analysis.Locale, field.Field)));
                }
            }
        }

        if (checks.Count == before)
        {
            checks.Add(Passed(
                ValidationCheckNames.VariablesDeclared,
                "Every variable used by the content is declared in the variables schema."));
        }
    }

    private static void AddUnusedVariableChecks(
        List<ValidationCheck> checks,
        IReadOnlyList<VariableDeclaration> declarations,
        IReadOnlyList<ContentAnalysis> analyses)
    {
        HashSet<string> used = new(
            analyses.SelectMany(analysis => analysis.Fields).SelectMany(field => field.UsedVariables),
            StringComparer.Ordinal);
        var before = checks.Count;
        foreach (VariableDeclaration declaration in declarations.Where(declaration => !used.Contains(declaration.Name)))
        {
            checks.Add(new ValidationCheck(
                ValidationCheckNames.VariablesUsed,
                ValidationCheckStatuses.Warning,
                $"Variable '{declaration.Name}' is declared but never used.",
                null));
        }

        if (checks.Count == before)
        {
            checks.Add(Passed(ValidationCheckNames.VariablesUsed, "Every declared variable is used by the content."));
        }
    }

    private static void AddUrlChecks(
        List<ValidationCheck> checks,
        Template template,
        TemplateVersion version,
        IReadOnlyList<VariableDeclaration> declarations,
        IReadOnlyList<ContentAnalysis> analyses)
    {
        var before = checks.Count;
        foreach (TemplateContent content in version.Contents)
        {
            foreach ((var field, var text) in Fields(content))
            {
                foreach (Match match in LiteralLink().Matches(text))
                {
                    ValidationCheck? finding = CheckLink(
                        template,
                        match.Groups[1].Value,
                        At(content.Channel, content.Locale, field));
                    if (finding is not null)
                    {
                        checks.Add(finding);
                    }
                }
            }
        }

        AddUrlVariableChecks(checks, template, declarations, analyses);
        if (checks.Count == before)
        {
            checks.Add(Passed(ValidationCheckNames.UrlAllowlist, "Links and URL variables respect the allowed domains."));
        }
    }

    private static ValidationCheck? CheckLink(Template template, string host, string location)
    {
        if (template.Class == NotificationClass.Critical)
        {
            return Failed(ValidationCheckNames.UrlAllowlist, "Links are not allowed in critical templates.", location);
        }

        if (host.Contains('{', StringComparison.Ordinal))
        {
            return Failed(
                ValidationCheckNames.UrlAllowlist,
                "A link host must be a literal domain, not a variable.",
                location);
        }

        if (template.LinkDomainsAllowed.Count == 0)
        {
            return Failed(
                ValidationCheckNames.UrlAllowlist,
                "The content contains a link but the template allows no link domains.",
                location);
        }

        return template.IsLinkDomainAllowed(host)
            ? null
            : Failed(ValidationCheckNames.UrlAllowlist, $"Link host '{host}' is outside the allowed domains.", location);
    }

    private static void AddUrlVariableChecks(
        List<ValidationCheck> checks,
        Template template,
        IReadOnlyList<VariableDeclaration> declarations,
        IReadOnlyList<ContentAnalysis> analyses)
    {
        HashSet<string> urlVariables = new(
            declarations.Where(declaration => declaration.IsUrl).Select(declaration => declaration.Name),
            StringComparer.Ordinal);
        if (urlVariables.Count == 0)
        {
            return;
        }

        foreach (ContentAnalysis analysis in analyses)
        {
            foreach (ContentFieldAnalysis field in analysis.Fields)
            {
                foreach (var variable in field.UsedVariables.Where(urlVariables.Contains))
                {
                    var location = At(analysis.Channel, analysis.Locale, field.Field);
                    if (template.Class == NotificationClass.Critical)
                    {
                        checks.Add(Failed(
                            ValidationCheckNames.UrlAllowlist,
                            $"Variable '{variable}' carries a URL and links are not allowed in critical templates.",
                            location));
                    }
                    else if (template.LinkDomainsAllowed.Count == 0)
                    {
                        checks.Add(Failed(
                            ValidationCheckNames.UrlAllowlist,
                            $"Variable '{variable}' carries a URL but the template allows no link domains.",
                            location));
                    }
                }
            }
        }
    }

    private static void AddSensitiveVariableChecks(List<ValidationCheck> checks, Template template, TemplateVersion version)
    {
        var before = checks.Count;
        foreach (var variable in template.SensitiveVariables)
        {
            var inUrlPosition = new Regex(
                @"https?://[^\s<>""']*\{\{[^{}]*\b" + Regex.Escape(variable) + @"\b",
                RegexOptions.None,
                TimeSpan.FromSeconds(1));
            foreach (TemplateContent content in version.Contents)
            {
                foreach ((var field, var text) in Fields(content))
                {
                    if (inUrlPosition.IsMatch(text))
                    {
                        checks.Add(Failed(
                            ValidationCheckNames.SensitiveVariables,
                            $"Sensitive variable '{variable}' must not appear in a URL position.",
                            At(content.Channel, content.Locale, field)));
                    }
                }
            }
        }

        if (checks.Count == before)
        {
            checks.Add(Passed(ValidationCheckNames.SensitiveVariables, "No sensitive variable appears in a URL position."));
        }
    }

    private static void AddChannelLimitChecks(List<ValidationCheck> checks, TemplateVersion version)
    {
        var before = checks.Count;
        foreach (TemplateContent content in version.Contents)
        {
            if (content.Channel == Channel.Sms && content.Body.Length > SmsMaxBodyChars)
            {
                checks.Add(Failed(
                    ValidationCheckNames.ChannelLimits,
                    $"SMS body template exceeds {SmsMaxBodyChars} characters.",
                    At(content.Channel, content.Locale, TemplateContentFields.Body)));
            }

            if (content.Channel == Channel.Push && content.Subject is { Length: > PushMaxSubjectChars })
            {
                checks.Add(Failed(
                    ValidationCheckNames.ChannelLimits,
                    $"Push title template exceeds {PushMaxSubjectChars} characters.",
                    At(content.Channel, content.Locale, TemplateContentFields.Subject)));
            }

            if (content.Channel == Channel.Push && content.Body.Length > PushMaxBodyChars)
            {
                checks.Add(Failed(
                    ValidationCheckNames.ChannelLimits,
                    $"Push body template exceeds {PushMaxBodyChars} characters.",
                    At(content.Channel, content.Locale, TemplateContentFields.Body)));
            }

            if (content.Channel == Channel.Email && string.IsNullOrWhiteSpace(content.BodyText))
            {
                checks.Add(Failed(
                    ValidationCheckNames.ChannelLimits,
                    "Email content requires a plain-text version.",
                    At(content.Channel, content.Locale, TemplateContentFields.BodyText)));
            }
        }

        if (checks.Count == before)
        {
            checks.Add(Passed(ValidationCheckNames.ChannelLimits, "Content respects the per-channel size limits."));
        }
    }

    private static void AddDefaultLocaleChecks(List<ValidationCheck> checks, Template template, TemplateVersion version)
    {
        if (version.Contents.Count == 0)
        {
            checks.Add(Failed(ValidationCheckNames.DefaultLocale, "The version has no content to validate.", null));
            return;
        }

        if (template.DefaultLocale is null)
        {
            checks.Add(Failed(ValidationCheckNames.DefaultLocale, "The template declares no default locale.", null));
            return;
        }

        var before = checks.Count;
        foreach (Channel channel in version.Contents.Select(content => content.Channel).Distinct())
        {
            var covered = version.Contents.Any(content =>
                content.Channel == channel && content.Locale == template.DefaultLocale);
            if (!covered)
            {
                checks.Add(Failed(
                    ValidationCheckNames.DefaultLocale,
                    $"Channel '{channel.Value}' has no content for the default locale '{template.DefaultLocale.Value}'.",
                    channel.Value));
            }
        }

        if (checks.Count == before)
        {
            checks.Add(Passed(
                ValidationCheckNames.DefaultLocale,
                "Every active channel has content for the default locale."));
        }
    }

    private static void AddLayoutReferenceChecks(
        List<ValidationCheck> checks,
        TemplateVersion version,
        LayoutReferenceFacts? facts)
    {
        // A version without a layout reference is valid on its own; the check
        // only exists when there is a pin to verify (mirrors the schema check).
        if (version.LayoutKey is null || facts is null)
        {
            return;
        }

        if (!facts.LayoutExists)
        {
            checks.Add(Failed(
                ValidationCheckNames.LayoutReference,
                $"Layout '{facts.LayoutKey}' does not exist.",
                null));
            return;
        }

        if (!facts.VersionExists)
        {
            checks.Add(Failed(
                ValidationCheckNames.LayoutReference,
                $"Layout '{facts.LayoutKey}' has no version {facts.LayoutVersion}.",
                null));
            return;
        }

        if (!string.Equals(facts.VersionStatus, LayoutVersionStatuses.Published, StringComparison.Ordinal))
        {
            checks.Add(Failed(
                ValidationCheckNames.LayoutReference,
                $"Version {facts.LayoutVersion} of layout '{facts.LayoutKey}' is "
                + $"'{facts.VersionStatus}', not published.",
                null));
            return;
        }

        var before = checks.Count;
        foreach (TemplateContent content in version.Contents)
        {
            var available = facts.Contents
                .Where(unit => string.Equals(unit.Channel, content.Channel.Value, StringComparison.Ordinal))
                .Select(unit => Locale.Trusted(unit.Locale))
                .ToList();
            Locale? resolved = LocaleResolution.Resolve(
                content.Locale,
                available,
                facts.DefaultLocale is null ? null : Locale.Trusted(facts.DefaultLocale));
            if (resolved is null)
            {
                checks.Add(Failed(
                    ValidationCheckNames.LayoutReference,
                    $"Layout '{facts.LayoutKey}' version {facts.LayoutVersion} has no content "
                    + $"that resolves for ({content.Channel.Value}, {content.Locale.Value}).",
                    $"{content.Channel.Value}/{content.Locale.Value}"));
            }
        }

        if (checks.Count == before)
        {
            checks.Add(Passed(
                ValidationCheckNames.LayoutReference,
                "The pinned layout version is published and covers every content entry."));
        }
    }

    private static IEnumerable<(string Field, string Text)> Fields(TemplateContent content)
    {
        if (!string.IsNullOrEmpty(content.Subject))
        {
            yield return (TemplateContentFields.Subject, content.Subject);
        }

        yield return (TemplateContentFields.Body, content.Body);
        if (!string.IsNullOrEmpty(content.BodyText))
        {
            yield return (TemplateContentFields.BodyText, content.BodyText);
        }
    }

    private static string At(Channel channel, Locale locale, string field)
        => $"{channel.Value}/{locale.Value}/{field}";

    private static ValidationCheck Passed(string name, string message)
        => new(name, ValidationCheckStatuses.Passed, message, null);

    private static ValidationCheck Failed(string name, string message, string? location)
        => new(name, ValidationCheckStatuses.Failed, message, location);

    [GeneratedRegex(@"https?://([^\s/:?#<>""']+)")]
    private static partial Regex LiteralLink();
}
