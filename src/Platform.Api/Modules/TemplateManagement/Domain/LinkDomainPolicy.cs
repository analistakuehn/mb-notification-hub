using System.Text.Json;
using System.Text.RegularExpressions;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// The single rule that decides which hosts a text offers to a reader, and
/// which of them the template's allowed domains accept. Publication and render
/// both read it, so an address the catalog refuses cannot arrive later through
/// a variable value: a divergence would leave the allowlist enforced on one
/// side only, with nothing to signal it.
/// <para>
/// The detector here is deliberately narrower than the one that bans anything
/// clickable from an authentication SMS. That ban wants every shape a reader
/// might tap and pays for it in false positives; this rule refuses a
/// publication and blocks a render, so a document or invoice number read as a
/// host would turn a publication rule into an outage. The two error budgets
/// point in opposite directions and stay in separate detectors.
/// </para>
/// </summary>
public static partial class LinkDomainPolicy
{
    /// <summary>
    /// Stands in for the host of a candidate that resolved to nothing. It
    /// never reaches the allowlist as a domain: it is refused on sight, which
    /// is what keeps an empty capture and a detector timeout failing closed.
    /// </summary>
    public const string UnresolvedHost = "?";

    // A host that announces itself with neither a scheme nor a leading "//" is
    // only a host when its last label is one a reader would plausibly click.
    // Without this gate every Brazilian document, invoice, clause, and street
    // number ("12.345.678/0001-90", "1.234/56", "3.1/b") reads as a link, and
    // the allowlist stops being a publication rule and becomes an outage.
    private static readonly HashSet<string> PlausibleSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "br", "com", "net", "org", "io", "co", "gov", "edu", "app", "dev", "me", "ly", "tv", "cc",
        "info", "biz", "xyz", "link", "site", "shop", "store", "online", "club", "top", "us", "uk",
        "de", "fr", "es", "it", "pt", "ar", "cl", "mx",
    };

    /// <summary>Every host the text offers to a reader, in reading order.</summary>
    public static IReadOnlyList<string> HostsIn(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        List<string> hosts = [];
        try
        {
            for (Match match = HostCandidate().Match(text); match.Success; match = match.NextMatch())
            {
                var announced = match.Value.StartsWith("//", StringComparison.Ordinal)
                    || match.Value.StartsWith("http", StringComparison.OrdinalIgnoreCase);
                var host = match.Groups["host"].Value;
                if (!announced && !HasPlausibleSuffix(host))
                {
                    continue;
                }

                // A blank host never leaves this method: the allowlist throws
                // on one, and the render path does not catch, so a blank host
                // reaching it turns a refusal into a poison message.
                hosts.Add(string.IsNullOrWhiteSpace(host) ? UnresolvedHost : host);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Fails closed, and stays on the Result axis, for the same reason
            // the authentication SMS detector answers "there is a link" here:
            // the caller runs inside a contract that handles no exception.
            return [UnresolvedHost];
        }

        return hosts;
    }

    /// <summary>
    /// The first host of any string value of the payload that the template
    /// does not allow, at any depth, array elements included; null when the
    /// payload offers nothing outside the allowed domains.
    /// </summary>
    public static string? FirstDisallowedHost(JsonElement? payload, Template template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (payload is not { } node)
        {
            return null;
        }

        if (node.ValueKind is JsonValueKind.Object)
        {
            foreach (JsonProperty property in node.EnumerateObject())
            {
                var found = FirstDisallowedHost(property.Value, template);
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }

        if (node.ValueKind is JsonValueKind.Array)
        {
            foreach (JsonElement item in node.EnumerateArray())
            {
                var found = FirstDisallowedHost(item, template);
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }

        // Every other kind carries no text a reader could act on.
        return node.ValueKind is JsonValueKind.String
            ? FirstDisallowedHost(node.GetString(), template)
            : null;
    }

    /// <summary>
    /// The first host of the text that the template does not allow, or null
    /// when the text offers nothing outside the allowed domains. A host that
    /// carries a placeholder, and one that resolved to nothing, are refused
    /// too: neither is a literal domain the allowlist can rule on.
    /// </summary>
    public static string? FirstDisallowedHost(string? text, Template template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        // Two phases, and the split is measured, not a preference: IsMatch
        // allocates no Match and answers no for the overwhelming majority of
        // values, so only a hit pays for the walk that lists every host.
        try
        {
            if (!HostCandidate().IsMatch(text))
            {
                return null;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return UnresolvedHost;
        }

        foreach (var host in HostsIn(text))
        {
            if (string.Equals(host, UnresolvedHost, StringComparison.Ordinal)
                || host.Contains('{', StringComparison.Ordinal)
                || !template.IsLinkDomainAllowed(host))
            {
                return host;
            }
        }

        return null;
    }

    /// <summary>
    /// The text with the HTML constructs that carry a URI without offering a
    /// link removed. A DOCTYPE names a public identifier and a DTD location, and
    /// an xmlns names a namespace; neither is something a reader can act on, and
    /// neither can be declared as an allowed domain, because an allowed domain
    /// must be a bare host with a dot and an alphabetic suffix. Scanning them as
    /// links would make every template that pins an XHTML layout impossible to
    /// publish, with no fix available to its author.
    /// </summary>
    public static string WithoutNonLinkUri(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        try
        {
            return XmlNamespaceDeclaration().Replace(
                DocumentTypeDeclaration().Replace(text, string.Empty),
                string.Empty);
        }
        catch (RegexMatchTimeoutException)
        {
            // Fails closed: the uncleaned text goes on to the scan, which then
            // sees every URI the markup carries and can only refuse more than
            // the rule asks for, never less.
            return text;
        }
    }

    /// <summary>
    /// The first host of a piece of HTML-bearing content that the template does
    /// not allow, ignoring the class-wide ban on links: a layout is shared
    /// framing whose links are already bounded by the pinning template's allowed
    /// domains, and a single CDN image would otherwise make every layout
    /// unusable by a critical template, with no allowlist able to fix it.
    /// </summary>
    public static string? FirstDisallowedHostInMarkup(string? text, Template template)
        => FirstDisallowedHost(WithoutNonLinkUri(text), template);

    /// <summary>Whether the value is an absolute http(s) URL inside the allowed domains.</summary>
    public static bool IsAllowedUrlValue(Template template, JsonElement value)
        => value.ValueKind == JsonValueKind.String
            && Uri.TryCreate(value.GetString(), UriKind.Absolute, out Uri? url)
            // Uri.TryCreate("//evil.com/x", UriKind.Absolute, out _) returns
            // true on Windows, with Scheme "file" and Host "evil.com". The
            // http(s) requirement below is what refuses it, so it carries the
            // check rather than restating what TryCreate already answered.
            && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
            && template.IsLinkDomainAllowed(url.Host);

    private static bool HasPlausibleSuffix(string host)
    {
        var lastDot = host.LastIndexOf('.');
        return lastDot > 0
            && lastDot < host.Length - 1
            && PlausibleSuffixes.Contains(host[(lastDot + 1)..]);
    }

    // Three shapes, in the order an attacker reaches for them: the announced
    // address, the scheme-relative one the browser completes on its own, and
    // the bare host followed by a path, which is what a link shortener hands
    // out. The host is one group across the three alternatives, because a
    // capture inside a loop keeps only its last occurrence.
    //
    // NonBacktracking is load-bearing, not a preference. The third alternative
    // nests quantifiers over dotted labels, and a backtracking engine walks it
    // quadratically over text a caller supplies through a variable at render
    // time. It also forbids lookaround, which is why the delimiter after a
    // bare host is consumed instead of looked at.
    [GeneratedRegex(
        @"https?://(?<host>[a-z0-9{][a-z0-9.\-{}]*)"
        + @"|//(?<host>[a-z0-9{][a-z0-9.\-{}]*)"
        + @"|\b(?<host>[a-z0-9][a-z0-9\-]*(?:\.[a-z0-9\-]+)*\.[a-z]{2,24})(?:[/:?#]|\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex HostCandidate();

    // The DOCTYPE runs to the first ">", which is where every standard
    // declaration ends: the public identifier and the DTD location it carries
    // are quoted and hold no ">" of their own.
    [GeneratedRegex(
        @"<!doctype[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex DocumentTypeDeclaration();

    // Both quoting styles in one alternation, because an attribute value is
    // delimited by the quote that opened it and nothing else may close it.
    [GeneratedRegex(
        @"\bxmlns(?::[a-z0-9_.-]+)?\s*=\s*""[^""]*"""
        + @"|\bxmlns(?::[a-z0-9_.-]+)?\s*=\s*'[^']*'",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex XmlNamespaceDeclaration();
}
