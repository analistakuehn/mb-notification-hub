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
    public const string SensitiveVariablesRetained = "sensitive-variables-retained";
    public const string ChannelLimits = "channel-limits";
    public const string DefaultLocale = "default-locale";
    public const string LayoutReference = "layout-reference";
    public const string OutputCulture = "output-culture";
    public const string ContentPlaceholder = "content-placeholder";
}

/// <summary>Sandbox analysis of one field of a content entry.</summary>
/// <param name="Field">Which field of the content entry this is.</param>
/// <param name="ParseSucceeded">Whether the sandbox could build a template from it.</param>
/// <param name="ParseError">What the sandbox said when it could not.</param>
/// <param name="UsedVariables">The variables the field reads.</param>
/// <param name="CultureArguments">
/// The formatting members the field hands a culture to. The render refuses
/// every one of them, so a version carrying one cannot produce a message;
/// naming them here is what turns that into something the publisher reads
/// before the version goes live rather than after.
/// </param>
public sealed record ContentFieldAnalysis(
    string Field,
    bool ParseSucceeded,
    string? ParseError,
    IReadOnlyList<string> UsedVariables,
    IReadOnlyList<string> CultureArguments);

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
    /// The purpose that makes a template carry authentication material, under
    /// the name this catalog and the go-live gate have always read it by. The
    /// value itself belongs to the published surface, where the predicate every
    /// reader asks lives, so there is one word and one place that defines it.
    /// </summary>
    public const string AuthenticationPurpose = TemplatePurposes.Authentication;

    /// <summary>
    /// Stable code of the refusal that a link inside an authentication SMS
    /// earns, at publication and at render alike, under the name this module
    /// has always read it by. The value itself belongs to the published
    /// surface, where the consuming module compares against it, so there is
    /// one word and one place that defines it.
    /// </summary>
    public const string AuthenticationSmsLinkCode = RenderedContentRejectionReasons.AuthenticationSmsLink;

    /// <summary>
    /// The refusal one culture argument earns, worded once and read at
    /// publication and at render alike. The sandbox refuses the same content it
    /// describes, so an author who meets this at publication and an author who
    /// meets it at render are told the same thing in the same words; two
    /// spellings of it would drift, and the one an author sees would depend on
    /// which door they arrived through.
    /// </summary>
    public static string CultureArgumentMessage(string member)
        => $"The content passes a culture to '{member}'. Formatting is invariant here, "
            + "so the culture argument is refused: remove it.";

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

    /// <remarks>
    /// <c>sensitiveVariablesInForce</c> carries what the version currently in
    /// force declares sensitive, when there is one. Only the callers that
    /// decide whether this version replaces it can read that, so they supply
    /// it, and the report can then say that publishing would drop a protection
    /// the template already has.
    /// </remarks>
    public static ValidationReport Validate(
        Template template,
        TemplateVersion version,
        IReadOnlyList<ContentAnalysis> analyses,
        LayoutReferenceFacts? layoutReference = null,
        IReadOnlyList<string>? sensitiveVariablesInForce = null)
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

        AddUrlChecks(checks, template, version, declarations, analyses, layoutReference);
        AddAuthenticationSmsChecks(checks, template, version, declarations, analyses, layoutReference);
        AddSensitiveVariableChecks(checks, version);
        AddSensitiveVariableRetentionChecks(checks, version, sensitiveVariablesInForce);
        AddChannelLimitChecks(checks, version, layoutReference);
        AddDefaultLocaleChecks(checks, template, version);
        AddLayoutReferenceChecks(checks, version, layoutReference);
        AddOutputCultureChecks(checks, analyses);
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

    /// <summary>
    /// Refuses content that picks its own culture. Formatting is invariant in
    /// this system, so a culture argument does not change the text a message
    /// carries, it stops the message from being produced at all: the same
    /// content is refused by the render. Failing it here costs a publication;
    /// letting it through costs a notification that nobody can send.
    /// </summary>
    private static void AddOutputCultureChecks(
        List<ValidationCheck> checks,
        IReadOnlyList<ContentAnalysis> analyses)
    {
        var before = checks.Count;
        foreach (ContentAnalysis analysis in analyses)
        {
            foreach (ContentFieldAnalysis field in analysis.Fields)
            {
                foreach (var member in field.CultureArguments)
                {
                    checks.Add(Failed(
                        ValidationCheckNames.OutputCulture,
                        CultureArgumentMessage(member),
                        At(analysis.Channel, analysis.Locale, field.Field)));
                }
            }
        }

        if (checks.Count == before)
        {
            checks.Add(Passed(
                ValidationCheckNames.OutputCulture,
                "No content picks a culture of its own for formatting."));
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
            : Failed(ValidationCheckNames.VariablesSchema, "The variables schema is not JSON this system can read.", null));
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

    /// <summary>
    /// The allowed domains rule over the content of the version and over the
    /// layout it pins alike: the reader receives one message, and a link the
    /// wrapper carries is as clickable as one the body carries.
    /// </summary>
    private static void AddUrlChecks(
        List<ValidationCheck> checks,
        Template template,
        TemplateVersion version,
        IReadOnlyList<VariableDeclaration> declarations,
        IReadOnlyList<ContentAnalysis> analyses,
        LayoutReferenceFacts? facts)
    {
        var before = checks.Count;
        HashSet<string> urlVariables = new(
            declarations.Where(declaration => declaration.IsUrl).Select(declaration => declaration.Name),
            StringComparer.Ordinal);
        HashSet<string> layoutUrlVariables = new(StringComparer.Ordinal);
        foreach (TemplateContent content in version.Contents)
        {
            foreach ((var field, var text) in Fields(content))
            {
                var linkText = content.Channel == Channel.Email && field == TemplateContentFields.Body
                    ? LinkDomainPolicy.MarkupForLinkScan(text)
                    : text;
                var location = At(content.Channel, content.Locale, field);
                foreach (var host in LinkDomainPolicy.HostsIn(linkText))
                {
                    ValidationCheck? finding = CheckLink(template, host, location);
                    if (finding is not null)
                    {
                        checks.Add(finding);
                    }
                }

                AddDynamicDestinationChecks(checks, text, urlVariables, location);
            }

            // The layout text answers to the allowed domains, and never to the
            // class-wide ban that CheckLink applies: framing is shared by many
            // templates, and a single logo from the CDN would otherwise put
            // every layout out of reach of a critical template, with no
            // allowlist entry able to undo the refusal.
            //
            // The two fields go through different doors of the link policy, and
            // the split is a fix rather than a nicety. The body is markup, so
            // the DOCTYPE and the xmlns declarations come out before the scan:
            // they name a DTD and a namespace, and no author can declare either
            // as an allowed domain. The text variant carries no markup, so
            // nothing comes out of it: an address written inside what looks
            // like a declaration is shielded by nothing there, and a client
            // that auto-links turns it into a link the reader can tap.
            if (facts is { PinIsUsable: true }
                && facts.ResolveContent(content.Channel, content.Locale) is { } framing)
            {
                if (LinkDomainPolicy.FirstDisallowedHostInMarkup(framing.Body, template) is { } inMarkup)
                {
                    checks.Add(LayoutHostFinding(inMarkup, AtLayout(facts, content, TemplateContentFields.Body)));
                }

                AddDynamicDestinationChecks(
                    checks,
                    framing.Body,
                    layoutUrlVariables,
                    AtLayout(facts, content, TemplateContentFields.Body));

                if (LinkDomainPolicy.FirstDisallowedHost(framing.BodyText, template) is { } inPlainText)
                {
                    checks.Add(LayoutHostFinding(
                        inPlainText, AtLayout(facts, content, TemplateContentFields.BodyText)));
                }

                if (framing.BodyText is not null)
                {
                    AddDynamicDestinationChecks(
                        checks,
                        framing.BodyText,
                        layoutUrlVariables,
                        AtLayout(facts, content, TemplateContentFields.BodyText));
                }
            }
        }

        AddUrlVariableChecks(checks, template, declarations, analyses);
        if (checks.Count == before)
        {
            checks.Add(Passed(ValidationCheckNames.UrlAllowlist, "Links and URL variables respect the allowed domains."));
        }

        static ValidationCheck LayoutHostFinding(string host, string location)
            => Failed(
                ValidationCheckNames.UrlAllowlist,
                $"The pinned layout carries link host '{host}', which is outside the allowed domains.",
                location);
    }

    private static ValidationCheck? CheckLink(Template template, string host, string location)
    {
        if (template.Class == NotificationClass.Critical)
        {
            return Failed(ValidationCheckNames.UrlAllowlist, "Links are not allowed in critical templates.", location);
        }

        if (host.Contains('{', StringComparison.Ordinal)
            || string.Equals(host, LinkDomainPolicy.UnresolvedHost, StringComparison.Ordinal))
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
    /// A destination assembled from source fragments has no declaration whose
    /// value can be checked before render. The only dynamic shape with that
    /// property is one complete global variable marked as a URL by the schema;
    /// every expression, suffix, prefix, or multi-variable composition fails
    /// closed here.
    /// </summary>
    private static void AddDynamicDestinationChecks(
        List<ValidationCheck> checks,
        string text,
        HashSet<string> urlVariables,
        string location)
    {
        foreach (var destination in DynamicDestinationsIn(text))
        {
            if (destination is not null
                && LinkDomainPolicy.HostsIn(destination).Any(host =>
                    host.Contains('{', StringComparison.Ordinal)
                    || string.Equals(host, LinkDomainPolicy.UnresolvedHost, StringComparison.Ordinal)))
            {
                // The literal-host check already reports this destination.
                // Keeping one finding per cause prevents a report from
                // presenting the same repair twice under different wording.
                continue;
            }

            if (TryWholeUrlVariable(destination, out var variable))
            {
                if (urlVariables.Contains(variable))
                {
                    continue;
                }

                checks.Add(Failed(
                    ValidationCheckNames.UrlAllowlist,
                    $"Variable '{variable}' is used as a link destination and must declare format 'url' or 'uri'.",
                    location));
                continue;
            }

            checks.Add(Failed(
                ValidationCheckNames.UrlAllowlist,
                "A dynamic link destination must be one complete global variable "
                + "declared with format 'url' or 'uri'.",
                location));
        }
    }

    private static List<string?> DynamicDestinationsIn(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("{{", StringComparison.Ordinal))
        {
            return [];
        }

        List<string?> destinations = [];
        try
        {
            foreach (Match match in DestinationValue().Matches(text))
            {
                var value = match.Groups["destination"].Value;
                if (value.Contains("{{", StringComparison.Ordinal))
                {
                    destinations.Add(value.Trim());
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // A source the bounded detector cannot inspect cannot be allowed
            // to carry a dynamic destination.
            return [null];
        }

        foreach (var destination in LinkDomainPolicy.CssUrlDestinationsIn(text))
        {
            if (destination is null || destination.Contains("{{", StringComparison.Ordinal))
            {
                destinations.Add(destination?.Trim());
            }
        }

        foreach (var destination in LinkDomainPolicy.MetaRefreshDestinationsIn(text))
        {
            if (destination is null || destination.Contains("{{", StringComparison.Ordinal))
            {
                destinations.Add(destination?.Trim());
            }
        }

        return destinations;
    }

    private static bool TryWholeUrlVariable(string? destination, out string variable)
    {
        variable = string.Empty;
        if (destination is null)
        {
            return false;
        }

        try
        {
            Match match = WholeUrlVariable().Match(destination);
            if (!match.Success)
            {
                return false;
            }

            variable = match.Groups["variable"].Value;
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
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
        IReadOnlyList<ContentAnalysis> analyses,
        LayoutReferenceFacts? facts)
    {
        if (!TemplatePurposes.IsAuthentication(template.Purpose)) return;

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

            // The wrapper reaches the same phone. Without this the catalog
            // says the version is clean and the render refuses it every time,
            // which reads as an authentication code that never arrives.
            //
            // Both fields go through one detector here, unlike the allowlist
            // rule above, which sends them through different doors. This ban
            // asks whether anything is clickable, not which domain it points
            // at, and it strips nothing from either field: an SMS wrapper is
            // not markup, so there is no declaration to shield a host and
            // nothing to take out before looking.
            if (facts is { PinIsUsable: true }
                && facts.ResolveContent(content.Channel, content.Locale) is { } framing)
            {
                foreach ((var field, var text) in LayoutFields(framing))
                {
                    if (ContainsLinkLikeText(text))
                    {
                        checks.Add(Failed(
                            ValidationCheckNames.AuthenticationSmsLinks,
                            "The layout this version pins puts a link inside an authentication SMS.",
                            AtLayout(facts, content, field)));
                    }
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

    private static void AddSensitiveVariableChecks(List<ValidationCheck> checks, TemplateVersion version)
    {
        if (version.SensitiveVariables.Count == 0)
        {
            checks.Add(Passed(ValidationCheckNames.SensitiveVariables, "The version declares no sensitive variable."));
            return;
        }

        var before = checks.Count;
        var sensitive = new HashSet<string>(version.SensitiveVariables, StringComparer.Ordinal);

        AddSensitiveVariableDeclarationChecks(checks, version);

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
    /// Refuses a version that stops declaring a name the version in force
    /// declares. Whether a template accepts ingestion over the bus is read off
    /// the published declaration, so a version that drops a name silently
    /// reopens that door for the whole template, and the render stops masking
    /// the value on every channel at the same moment. Neither effect appears
    /// anywhere in the report unless a check says so.
    /// <para>
    /// It answers nothing on a first publication, where there is no version in
    /// force and therefore nothing to regress from. What is missing there is a
    /// name nobody declared, which no check of this catalog can find: only a
    /// person reading the content can.
    /// </para>
    /// <para>
    /// Adding names is always allowed, and so is removing one in the same act
    /// that removes the variable: this compares declarations and never content,
    /// because a declaration is what the runtime reads.
    /// </para>
    /// </summary>
    private static void AddSensitiveVariableRetentionChecks(
        List<ValidationCheck> checks,
        TemplateVersion version,
        IReadOnlyList<string>? sensitiveVariablesInForce)
    {
        // Mirrors the layout-reference check: the rule exists only when there
        // is something to compare against.
        if (sensitiveVariablesInForce is null)
        {
            return;
        }

        var declared = new HashSet<string>(version.SensitiveVariables, StringComparer.Ordinal);
        var before = checks.Count;
        foreach (var variable in sensitiveVariablesInForce.Where(variable => !declared.Contains(variable)))
        {
            checks.Add(Failed(
                ValidationCheckNames.SensitiveVariablesRetained,
                $"Sensitive variable '{variable}' is declared by the version in force and dropped by this one. "
                + "Publishing it would stop the render from masking that value and reopen bus ingestion "
                + "for this template.",
                null));
        }

        if (checks.Count == before)
        {
            checks.Add(Passed(
                ValidationCheckNames.SensitiveVariablesRetained,
                "This version keeps every sensitive variable the version in force declares."));
        }
    }

    /// <summary>
    /// A sensitive name only masks when it addresses a variable the schema
    /// describes, because that is the shape the mask walks. A name the schema
    /// never declares therefore masks nothing, and the render stores the full
    /// form as if it were the masked one: the value travels in clear into an
    /// append-only trail that cannot be corrected afterwards. Refusing
    /// publication is the only point where this is still cheap.
    /// <para>
    /// The name resolves at any depth the schema declares, and a dotted name
    /// resolves as an absolute path. A name that resolves to nothing is still
    /// refused, including a dotted name whose intermediate level the schema
    /// declares as something other than a nested object: the mask cannot
    /// address a value through it either.
    /// </para>
    /// </summary>
    private static void AddSensitiveVariableDeclarationChecks(
        List<ValidationCheck> checks,
        TemplateVersion version)
    {
        if (!VariablesSchema.TryUndeclaredNames(
            version.VariablesSchemaJson, version.SensitiveVariables, out IReadOnlyList<string> undeclared))
        {
            // The schema itself is unusable, which `variables-schema` already
            // reports. Naming it twice would only crowd the report.
            return;
        }

        foreach (var variable in undeclared)
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
                // The read is matched both whole and segment by segment: a
                // dotted sensitive name addresses the whole read, and a name
                // without a dot addresses any segment of it, which is exactly
                // the pair of shapes the mask reaches.
                if (sensitive.Contains(identifier.Value))
                {
                    yield return identifier.Value;
                    continue;
                }

                foreach (var segment in identifier.Value.Split('.'))
                {
                    if (sensitive.Contains(segment))
                    {
                        yield return segment;
                    }
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

    /// <summary>
    /// What leaves the platform is the content inside its wrapper, so the
    /// ceiling applies to the sum. The measure stays on the source, which is
    /// what this catalog has always measured, and the sum keeps the characters
    /// of the content placeholder in: a layout may read the placeholder more
    /// than once, so discounting it once would understate the message. The
    /// result is conservative by that much.
    /// <para>
    /// The messages say "template source" and mean it. This check counts the
    /// characters an author wrote, placeholders included, and it can say
    /// nothing about the message a recipient receives: the values that replace
    /// the placeholders are not known here, and the same source renders to a
    /// different length for every request. What the rendered message costs is
    /// answered at render time, in segments, which is the unit a carrier
    /// actually splits and bills.
    /// </para>
    /// </summary>
    private static void AddChannelLimitChecks(
        List<ValidationCheck> checks,
        TemplateVersion version,
        LayoutReferenceFacts? facts)
    {
        var before = checks.Count;
        foreach (TemplateContent content in version.Contents)
        {
            LayoutContentFacts? framing = facts is { PinIsUsable: true }
                ? facts.ResolveContent(content.Channel, content.Locale)
                : null;
            var wrapped = content.Body.Length + (framing?.Body.Length ?? 0);

            if (content.Channel == Channel.Sms && wrapped > SmsMaxBodyChars)
            {
                checks.Add(Failed(
                    ValidationCheckNames.ChannelLimits,
                    framing is null
                        ? $"The SMS body template source exceeds {SmsMaxBodyChars} characters."
                        : "The SMS body template source and the layout it pins exceed "
                            + $"{SmsMaxBodyChars} characters together.",
                    At(content.Channel, content.Locale, TemplateContentFields.Body)));
            }

            if (content.Channel == Channel.Push && content.Subject is { Length: > PushMaxSubjectChars })
            {
                checks.Add(Failed(
                    ValidationCheckNames.ChannelLimits,
                    $"Push title template exceeds {PushMaxSubjectChars} characters.",
                    At(content.Channel, content.Locale, TemplateContentFields.Subject)));
            }

            if (content.Channel == Channel.Push && wrapped > PushMaxBodyChars)
            {
                checks.Add(Failed(
                    ValidationCheckNames.ChannelLimits,
                    framing is null
                        ? $"Push body template exceeds {PushMaxBodyChars} characters."
                        : $"Push body template and the layout it pins exceed {PushMaxBodyChars} characters together.",
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

        // The identity answers before its versions, because the answer does
        // not change with the version the author picks: a layout in either of
        // these states is no target for a reference at all, and saying that
        // first is what stops the author from hunting for a version that
        // publishes.
        if (string.Equals(facts.LayoutStatus, LayoutStatuses.Disabled, StringComparison.Ordinal))
        {
            checks.Add(Failed(
                ValidationCheckNames.LayoutReference,
                $"Layout '{facts.LayoutKey}' is disabled and frames no message any more.",
                null));
            return;
        }

        if (string.Equals(facts.LayoutStatus, LayoutStatuses.Deprecated, StringComparison.Ordinal))
        {
            checks.Add(Failed(
                ValidationCheckNames.LayoutReference,
                $"Layout '{facts.LayoutKey}' is deprecated and takes no new reference.",
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
            if (facts.ResolveContent(content.Channel, content.Locale) is null)
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

    /// <summary>
    /// Fields of the pinned layout content that carry text a reader can act on.
    /// </summary>
    private static IEnumerable<(string Field, string Text)> LayoutFields(LayoutContentFacts framing)
    {
        yield return (TemplateContentFields.Body, framing.Body);
        if (!string.IsNullOrEmpty(framing.BodyText))
        {
            yield return (TemplateContentFields.BodyText, framing.BodyText);
        }
    }

    private static string At(Channel channel, Locale locale, string field)
        => $"{channel.Value}/{locale.Value}/{field}";

    /// <summary>
    /// Location of a finding about text the template author did not write.
    /// Naming the layout and its version is what keeps them from searching
    /// their own content for a link that is not there.
    /// </summary>
    private static string AtLayout(LayoutReferenceFacts facts, TemplateContent content, string field)
        => $"layout:{facts.LayoutKey}@{facts.LayoutVersion}/{content.Channel.Value}/{content.Locale.Value}/{field}";

    private static ValidationCheck Passed(string name, string message)
        => new(name, ValidationCheckStatuses.Passed, message, null);

    private static ValidationCheck Failed(string name, string message, string? location)
        => new(name, ValidationCheckStatuses.Failed, message, location);

    // No expression in this file carries a match timeout, and that is a
    // correctness rule rather than a preference. NonBacktracking already
    // guarantees linear time, which is the whole reason it is here, so a
    // timeout buys nothing; and measured on this runtime the two together
    // answer "no match" instead of matching or throwing once the stretch
    // before the first match passes roughly a hundred thousand characters.
    // A refusal that never throws never reaches a catch, so the guard reads
    // "no match" as "no host" and approves, which turns failing closed into
    // a silent approval an author can switch on by writing a long enough
    // body. Do not add one back for symmetry with a neighbour.
    //
    // Three shapes, in the order an attacker reaches for them: the full
    // address, the host that only announces itself with www, and the bare host
    // followed by a path, which is what every link shortener produces.
    //
    // The first shape accepts any run of slashes and backslashes after the
    // scheme, the empty run included, because that is what a client accepts
    // and what it resolves to a host. Demanding the two canonical slashes
    // banned the spelling that carries them and let the same address through
    // under every other one, in the single class where a link is banned
    // outright. The trailing character excludes a slash so that the literal
    // text of a scheme with nothing after it stays prose.
    //
    // The second shape is the same address with the scheme left off, which a
    // client resolves against the message it arrived in. It answers only at a
    // value boundary, so a doubled slash inside a path already matched, and an
    // arithmetic ratio written in the body, stay what they are.
    //
    // NonBacktracking is load-bearing, not a preference. The third alternative
    // nests quantifiers, and a backtracking engine walks it quadratically over
    // a long run of dotted labels with no trailing slash, which is text a
    // caller can supply through a variable at render time.
    [GeneratedRegex(
        @"https?:[/\\]*[^\s/\\]"
        + @"|(?:\A|[\s""'=(\[>])[/\\]{2,}[^\s/\\]"
        + @"|\bwww\.[a-z0-9-]+\.[a-z]{2,}|\b[a-z0-9][a-z0-9-]*(?:\.[a-z0-9-]+)+/",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex LinkLike();

    /// <summary>Placeholders and their inner expression; linear by construction.</summary>
    [GeneratedRegex(@"\{\{([^{}]*)\}\}", RegexOptions.NonBacktracking)]
    private static partial Regex Placeholder();

    /// <summary>Identifiers read inside one placeholder expression.</summary>
    [GeneratedRegex(
        @"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*",
        RegexOptions.NonBacktracking)]
    private static partial Regex Identifier();

    /// <summary>
    /// URI-bearing HTML attributes, CSS url() values, and Markdown link
    /// targets. Each alternative consumes a bounded value without nesting or
    /// backtracking; the source can arrive from an authoring request.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:href|src|action|formaction|poster|cite|background|data|longdesc|manifest|ping|srcset)\s*=\s*"
        + @"(?:""(?<destination>[^""]*)""|'(?<destination>[^']*)'|(?<destination>[^\s>]+))"
        + @"|\]\(\s*(?:<(?<destination>[^>\r\n]*)>|(?<destination>\{\{[^{}\r\n]*\}\})"
        + @"|(?<destination>[^\s)\r\n]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex DestinationValue();

    /// <summary>One complete read of a top-level global, with optional whitespace trimming.</summary>
    [GeneratedRegex(
        @"\A\{\{~?\s*(?<variable>[A-Za-z_][A-Za-z0-9_]*)\s*~?\}\}\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex WholeUrlVariable();
}
