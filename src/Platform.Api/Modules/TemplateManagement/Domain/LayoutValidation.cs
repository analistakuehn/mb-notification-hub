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
        AddLinkHostChecks(checks, version);
        AddChannelLimitChecks(checks, version);
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

    /// <summary>
    /// Refuses layout content that picks its own culture, on the same terms and
    /// with the same sentence the template catalog uses. A layout renders on
    /// the same engine and is refused by the same ban, so a layout left out
    /// here would reach publication clean and break every template that pins
    /// it.
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
                        TemplateValidation.CultureArgumentMessage(member),
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

    /// <summary>
    /// Names every link host the wrapper offers, as a warning and never as a
    /// refusal. A layout declares no allowed domain of its own: the allowlist
    /// belongs to each template that pins it, so this catalog has no operand to
    /// decide with and can only tell the author what every one of those
    /// templates will have to allow. The finding still belongs here, because
    /// the refusal otherwise reaches a different person at a later moment, on a
    /// template whose author did not write the offending text.
    /// <para>
    /// The narrow detector answers here, the one that asks for an alphabetic
    /// suffix, and not the wide one that bans anything clickable from an
    /// authentication SMS. A warning that informs has to be precise or it
    /// trains the author to skip it, and the wide detector reads the document
    /// numbers of an institutional footer as links.
    /// </para>
    /// <para>
    /// The body is markup and the text variant is not, so they go through
    /// different doors: a DOCTYPE and an xmlns carry a URI no reader can act
    /// on, while the same characters in plain text are an address a client can
    /// turn into a link.
    /// </para>
    /// </summary>
    private static void AddLinkHostChecks(List<ValidationCheck> checks, LayoutVersion version)
    {
        var before = checks.Count;
        foreach (LayoutContent content in version.Contents)
        {
            AddHostsOf(
                LinkDomainPolicy.WithoutNonLinkUri(content.Body),
                At(content.Channel, content.Locale, TemplateContentFields.Body));
            AddHostsOf(content.BodyText, At(content.Channel, content.Locale, TemplateContentFields.BodyText));
        }

        if (checks.Count == before)
        {
            checks.Add(Passed(ValidationCheckNames.UrlAllowlist, "The layout offers no link host."));
        }

        void AddHostsOf(string? text, string location)
        {
            // One line per host, not per occurrence, and only hosts that are a
            // literal domain. A host built from a placeholder is not something
            // a domain list can accept, so naming it as one to allow would send
            // the author after a fix that does not exist; the template that
            // pins the layout refuses it through its own check.
            foreach (var host in LinkDomainPolicy.HostsIn(text)
                .Where(host => !host.Contains('{', StringComparison.Ordinal)
                    && !string.Equals(host, LinkDomainPolicy.UnresolvedHost, StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                checks.Add(new ValidationCheck(
                    ValidationCheckNames.UrlAllowlist,
                    ValidationCheckStatuses.Warning,
                    $"This layout carries link host '{host}'; every template that pins it must allow that domain.",
                    location));
            }
        }
    }

    /// <summary>
    /// The wrapper is the floor of every message that lands inside it: when
    /// the layout alone is already past the channel ceiling, no template
    /// content fits, and the refusal belongs here rather than on the author of
    /// each template that pins it.
    /// <para>
    /// Two limits of the template catalog have nothing to measure here. A
    /// layout carries no subject, and the render never wraps one, so the push
    /// title limit does not transfer. The plain-text requirement of e-mail
    /// does not transfer either: the text wrapper of a layout is optional by
    /// contract, and the render only wraps the text when the template and the
    /// layout both carry one.
    /// </para>
    /// </summary>
    private static void AddChannelLimitChecks(List<ValidationCheck> checks, LayoutVersion version)
    {
        var before = checks.Count;
        foreach (LayoutContent content in version.Contents)
        {
            if (content.Channel == Channel.Sms && content.Body.Length > TemplateValidation.SmsMaxBodyChars)
            {
                checks.Add(Failed(
                    ValidationCheckNames.ChannelLimits,
                    $"SMS layout body exceeds {TemplateValidation.SmsMaxBodyChars} characters.",
                    At(content.Channel, content.Locale, TemplateContentFields.Body)));
            }

            if (content.Channel == Channel.Push && content.Body.Length > TemplateValidation.PushMaxBodyChars)
            {
                checks.Add(Failed(
                    ValidationCheckNames.ChannelLimits,
                    $"Push layout body exceeds {TemplateValidation.PushMaxBodyChars} characters.",
                    At(content.Channel, content.Locale, TemplateContentFields.Body)));
            }
        }

        if (checks.Count == before)
        {
            checks.Add(Passed(
                ValidationCheckNames.ChannelLimits,
                "Every wrapper leaves room inside the per-channel size limits."));
        }
    }

    private static string At(Channel channel, Locale locale, string field)
        => $"{channel.Value}/{locale.Value}/{field}";

    private static ValidationCheck Passed(string name, string message)
        => new(name, ValidationCheckStatuses.Passed, message, null);

    private static ValidationCheck Failed(string name, string message, string? location)
        => new(name, ValidationCheckStatuses.Failed, message, location);
}
