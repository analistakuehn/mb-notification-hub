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
    public const string VariablesRequired = "variables-required";
    public const string VariablesTypes = "variables-types";
    public const string UrlAllowlist = "url-allowlist";
    public const string AuthenticationSmsLinks = "authentication-sms-links";
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
    /// <summary>
    /// How far back a URL scheme may sit from the placeholder it wraps. Bounds
    /// the backward scan, so a field carrying no whitespace at all cannot make
    /// the sensitive-variable check quadratic in its own length.
    /// </summary>
    private const int MaxUrlPrefixScan = 2048;

    public const int SmsMaxBodyChars = 1600;
    public const int PushMaxSubjectChars = 200;
    public const int PushMaxBodyChars = 4000;

    /// <summary>
    /// Purpose that makes a template carry authentication material. It is the
    /// same word the notification lifecycle routes by, declared here because
    /// this catalog has to recognize it without reaching into another context.
    /// </summary>
    public const string AuthenticationPurpose = "authentication";

    /// <summary>
    /// Stable code of the refusal that a link inside an authentication SMS
    /// earns, at publication and at render alike. A false positive costs one
    /// authentication code; a false negative hands an attacker the one message
    /// people are trained to trust and act on immediately, so the answer is to
    /// refuse.
    /// </summary>
    public const string AuthenticationSmsLinkCode = "authentication-sms-link";

    /// <summary>
    /// Whether a piece of text offers something to click. Wider than the link
    /// check of the allowlist on purpose, and used only where a link is banned
    /// outright: SMS phishing rarely writes a scheme, it writes a shortener or
    /// a bare host with a path, and matching only <c>https://</c> would call
    /// that clean.
    /// </summary>
    public static bool ContainsLinkLikeText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            return LinkLike().IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            // Fails closed, and stays on the Result axis. This predicate also
            // runs at dispatch, inside a contract whose consumer does not
            // handle exceptions: letting one escape turns a single message into
            // a poison message that burns a worker on every redelivery.
            return true;
        }
    }

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
        AddAuthenticationSmsChecks(checks, template, version, declarations, analyses);
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

    /// <summary>
    /// Bans anything clickable from the SMS content of an authentication
    /// template. The check exists only where the ban applies, like the layout
    /// reference check: a template of another purpose, or a version with no
    /// SMS content, has nothing here to answer for.
    /// <para>
    /// Both shapes of a link fail: one written into the content, and one
    /// arriving through a variable declared as a URL. The second would leave
    /// the source clean and still put an address in front of the person.
    /// </para>
    /// </summary>
    private static void AddAuthenticationSmsChecks(
        List<ValidationCheck> checks,
        Template template,
        TemplateVersion version,
        IReadOnlyList<VariableDeclaration> declarations,
        IReadOnlyList<ContentAnalysis> analyses)
    {
        if (!string.Equals(template.Purpose, AuthenticationPurpose, StringComparison.Ordinal)) return;

        var smsContents = version.Contents
            .Where(content => content.Channel == Channel.Sms)
            .ToList();
        if (smsContents.Count == 0) return;

        var before = checks.Count;
        foreach (TemplateContent content in smsContents)
        {
            foreach ((var field, var text) in Fields(content))
            {
                if (ContainsLinkLikeText(text))
                {
                    checks.Add(Failed(
                        ValidationCheckNames.AuthenticationSmsLinks,
                        "An authentication SMS must carry no link.",
                        At(content.Channel, content.Locale, field)));
                }
            }
        }

        HashSet<string> urlVariables = new(
            declarations.Where(declaration => declaration.IsUrl).Select(declaration => declaration.Name),
            StringComparer.Ordinal);
        if (urlVariables.Count > 0)
        {
            foreach (ContentAnalysis analysis in analyses.Where(analysis => analysis.Channel == Channel.Sms))
            {
                foreach (ContentFieldAnalysis field in analysis.Fields)
                {
                    foreach (var variable in field.UsedVariables.Where(urlVariables.Contains))
                    {
                        checks.Add(Failed(
                            ValidationCheckNames.AuthenticationSmsLinks,
                            $"Variable '{variable}' carries a URL and an authentication SMS must carry no link.",
                            At(analysis.Channel, analysis.Locale, field.Field)));
                    }
                }
            }
        }

        if (checks.Count == before)
        {
            checks.Add(Passed(
                ValidationCheckNames.AuthenticationSmsLinks,
                "The authentication SMS content offers nothing to click."));
        }
    }

    private static void AddSensitiveVariableChecks(List<ValidationCheck> checks, Template template, TemplateVersion version)
    {
        if (template.SensitiveVariables.Count == 0)
        {
            checks.Add(Passed(ValidationCheckNames.SensitiveVariables, "The template declares no sensitive variable."));
            return;
        }

        var before = checks.Count;
        var sensitive = new HashSet<string>(template.SensitiveVariables, StringComparer.Ordinal);

        AddSensitiveVariableDeclarationChecks(checks, template, version);

        foreach (TemplateContent content in version.Contents)
        {
            foreach ((var field, var text) in Fields(content))
            {
                foreach (var variable in SensitiveVariablesInUrlPosition(text, sensitive))
                {
                    checks.Add(Failed(
                        ValidationCheckNames.SensitiveVariables,
                        $"Sensitive variable '{variable}' must not appear in a URL position.",
                        At(content.Channel, content.Locale, field)));
                }
            }
        }

        if (checks.Count == before)
        {
            checks.Add(Passed(
                ValidationCheckNames.SensitiveVariables,
                "Every sensitive variable is declared by the schema and none appears in a URL position."));
        }
    }

    /// <summary>
    /// A sensitive name only masks when it addresses a variable the payload
    /// carries at its top level, because that is the shape the mask walks. A
    /// name the schema never declares therefore masks nothing, and the render
    /// stores the full form as if it were the masked one: the value travels in
    /// clear into an append-only trail that cannot be corrected afterwards.
    /// Refusing publication is the only point where this is still cheap.
    /// </summary>
    private static void AddSensitiveVariableDeclarationChecks(
        List<ValidationCheck> checks,
        Template template,
        TemplateVersion version)
    {
        if (!VariablesSchema.TryParse(version.VariablesSchemaJson, out IReadOnlyList<VariableDeclaration> declarations))
        {
            // The schema itself is unusable, which `variables-schema` already
            // reports. Naming it twice would only crowd the report.
            return;
        }

        var declared = new HashSet<string>(
            declarations.Select(declaration => declaration.Name),
            StringComparer.Ordinal);

        foreach (var variable in template.SensitiveVariables.Where(name => !declared.Contains(name)))
        {
            checks.Add(Failed(
                ValidationCheckNames.SensitiveVariables,
                $"Sensitive variable '{variable}' is not declared by the variables schema, so it would never be masked.",
                null));
        }
    }

    /// <summary>
    /// Sensitive variables read inside a placeholder that sits within a URL.
    /// Walks placeholders and looks back a bounded distance for the scheme,
    /// rather than asking one expression to span from the scheme to the name:
    /// that shape backtracks quadratically on a field carrying many schemes and
    /// no placeholder, which turns an authoring endpoint into a CPU sink.
    /// </summary>
    private static IEnumerable<string> SensitiveVariablesInUrlPosition(string text, HashSet<string> sensitive)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        foreach (Match placeholder in Placeholder().Matches(text))
        {
            if (!SitsInUrlPosition(text, placeholder.Index))
            {
                continue;
            }

            foreach (Match identifier in Identifier().Matches(placeholder.Groups[1].Value))
            {
                if (sensitive.Contains(identifier.Value))
                {
                    yield return identifier.Value;
                }
            }
        }
    }

    /// <summary>
    /// Whether the placeholder is preceded by a URL scheme with no delimiter in
    /// between, which is what puts its value inside the address rather than
    /// beside it.
    /// </summary>
    private static bool SitsInUrlPosition(string text, int placeholderStart)
    {
        var floor = Math.Max(0, placeholderStart - MaxUrlPrefixScan);
        var start = placeholderStart;
        while (start > floor && !IsUrlBoundary(text[start - 1]))
        {
            start--;
        }

        ReadOnlySpan<char> prefix = text.AsSpan(start, placeholderStart - start);
        return prefix.Contains("http://", StringComparison.OrdinalIgnoreCase)
            || prefix.Contains("https://", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUrlBoundary(char character)
        => char.IsWhiteSpace(character) || character is '<' or '>' or '"' or '\'';

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

    // Three shapes, in the order an attacker reaches for them: the full
    // address, the host that only announces itself with www, and the bare host
    // followed by a path, which is what every link shortener produces.
    //
    // NonBacktracking is load-bearing, not a preference. The third alternative
    // nests quantifiers, and a backtracking engine walks it quadratically over
    // a long run of dotted labels with no trailing slash, which is text a
    // caller can supply through a variable at render time.
    [GeneratedRegex(
        @"https?://\S|\bwww\.[a-z0-9-]+\.[a-z]{2,}|\b[a-z0-9][a-z0-9-]*(?:\.[a-z0-9-]+)+/",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex LinkLike();

    /// <summary>Placeholders and their inner expression; linear by construction.</summary>
    [GeneratedRegex(@"\{\{([^{}]*)\}\}", RegexOptions.NonBacktracking, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Placeholder();

    /// <summary>Identifiers read inside one placeholder expression.</summary>
    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.NonBacktracking, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Identifier();
}
