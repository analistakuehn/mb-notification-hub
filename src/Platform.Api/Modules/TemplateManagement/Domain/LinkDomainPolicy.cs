using System.Net;
using System.Text;
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
    private const string MalformedDestination = "https://%";

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

    private static readonly Dictionary<string, char> Html5UriPunctuation = new(StringComparer.Ordinal)
    {
        ["ast"] = '*',
        ["bsol"] = '\\',
        ["colon"] = ':',
        ["comma"] = ',',
        ["commat"] = '@',
        ["dollar"] = '$',
        ["equals"] = '=',
        ["excl"] = '!',
        ["lpar"] = '(',
        ["lowbar"] = '_',
        ["lsqb"] = '[',
        ["num"] = '#',
        ["percnt"] = '%',
        ["period"] = '.',
        ["plus"] = '+',
        ["quest"] = '?',
        ["rpar"] = ')',
        ["rsqb"] = ']',
        ["semi"] = ';',
        ["sol"] = '/',
        ["vert"] = '|',
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
            for (Match match = LinkCandidate().Match(text); match.Success; match = match.NextMatch())
            {
                if (match.Groups["announced"].Success)
                {
                    var authority = match.Groups["authority"].Value;
                    if (authority.Length == 0)
                    {
                        // A separator fragment such as "://" contains the
                        // protocol-relative marker but offers no destination.
                        // It remains inert until render composes an authority,
                        // which is where the final-form guard decides it.
                        continue;
                    }

                    if (authority.Contains('{', StringComparison.Ordinal)
                        || authority.Contains('}', StringComparison.Ordinal))
                    {
                        hosts.Add(UnresolvedHost);
                        continue;
                    }

                    if (!TryCanonicalHttpHost(
                        match.Groups["announced"].Value,
                        allowProtocolRelative: true,
                        out var host,
                        out var userInfo))
                    {
                        hosts.Add(UnresolvedHost);
                        continue;
                    }

                    // The Uri-derived host comes first so userinfo can never
                    // hide where the request goes. Host-looking userinfo still
                    // makes the value unsafe, but only the fixed marker may
                    // represent it: credentials are not safe error content.
                    hosts.Add(host);
                    foreach (Match userInfoHost in BareHostCandidate().Matches(userInfo))
                    {
                        var offered = userInfoHost.Groups["host"].Value;
                        if (HasPlausibleSuffix(offered))
                        {
                            hosts.Add(UnresolvedHost);
                            break;
                        }
                    }

                    continue;
                }

                var bareHost = match.Groups["host"].Value;
                if (!HasPlausibleSuffix(bareHost))
                {
                    continue;
                }

                // A blank host never leaves this method: the allowlist throws
                // on one, and the render path does not catch, so a blank host
                // reaching it turns a refusal into a poison message.
                hosts.Add(string.IsNullOrWhiteSpace(bareHost) ? UnresolvedHost : bareHost);
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
            if (!LinkCandidate().IsMatch(text))
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
        => FirstDisallowedHost(MarkupForLinkScan(text), template);

    /// <summary>
    /// Markup in the form a browser reads when deciding its destinations.
    /// Character references are decoded only after declarations that carry a
    /// non-clickable URI have been removed, so an encoded scheme in an
    /// attribute cannot hide a host without turning an XHTML namespace into a
    /// link at the same time.
    /// </summary>
    internal static string MarkupForLinkScan(string? text)
    {
        var markup = WithoutNonLinkUri(text);
        if (!TryNormalizeMarkupUriReferences(markup, out var normalizedMarkup))
        {
            return MalformedDestination;
        }

        markup = WebUtility.HtmlDecode(normalizedMarkup);
        CssUrlScanResult cssUrls = ScanCssUrlDestinations(markup);
        MetaRefreshScanResult refreshUrls = ScanMetaRefreshDestinations(markup);
        if (cssUrls.IsMalformed || refreshUrls.IsMalformed)
        {
            // Do not retain malformed input here. It can contain a credential,
            // personal value, or incomplete authority that must never become
            // the detail of the refusal raised by the next scan.
            return MalformedDestination;
        }

        foreach (MetaRefreshCarrier carrier in refreshUrls.Carriers)
        {
            if (!carrier.Destination.Contains("{{", StringComparison.Ordinal)
                && !TryCanonicalHttpHost(
                    carrier.Destination,
                    allowProtocolRelative: true,
                    out _,
                    out _))
            {
                return MalformedDestination;
            }
        }

        if (cssUrls.Carriers.Count == 0 && refreshUrls.Carriers.Count == 0)
        {
            return markup;
        }

        var scanText = new StringBuilder(
            markup.Length + cssUrls.DecodedLength + refreshUrls.DestinationLength);
        var markupIndex = 0;
        foreach (CssUrlCarrier carrier in cssUrls.Carriers)
        {
            scanText.Append(markup, markupIndex, carrier.Start - markupIndex);
            scanText.Append("url()");
            markupIndex = carrier.End;
        }

        scanText.Append(markup, markupIndex, markup.Length - markupIndex);
        foreach (CssUrlCarrier carrier in cssUrls.Carriers)
        {
            scanText.Append('\n');
            scanText.Append(carrier.Destination);
        }

        foreach (MetaRefreshCarrier carrier in refreshUrls.Carriers)
        {
            scanText.Append('\n');
            scanText.Append(carrier.Destination);
        }

        return scanText.ToString();
    }

    /// <summary>
    /// CSS url() destination values decoded by the same tokenizer used for the
    /// final markup decision. A null entry represents a malformed carrier.
    /// </summary>
    internal static IReadOnlyList<string?> CssUrlDestinationsIn(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        CssUrlScanResult scan = ScanCssUrlDestinations(text);
        if (scan.IsMalformed)
        {
            return [null];
        }

        List<string?> destinations = new(scan.Carriers.Count);
        foreach (CssUrlCarrier carrier in scan.Carriers)
        {
            destinations.Add(carrier.Destination);
        }

        return destinations;
    }

    /// <summary>
    /// Destinations carried by meta refresh elements, in source order. A null
    /// entry represents a refresh element whose URL parameter is unreadable.
    /// </summary>
    internal static IReadOnlyList<string?> MetaRefreshDestinationsIn(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        MetaRefreshScanResult scan = ScanMetaRefreshDestinations(text);
        if (scan.IsMalformed)
        {
            return [null];
        }

        List<string?> destinations = new(scan.Carriers.Count);
        foreach (MetaRefreshCarrier carrier in scan.Carriers)
        {
            destinations.Add(carrier.Destination);
        }

        return destinations;
    }

    /// <summary>Whether the value is an absolute http(s) URL inside the allowed domains.</summary>
    public static bool IsAllowedUrlValue(Template template, JsonElement value)
        => value.ValueKind == JsonValueKind.String
            && TryCanonicalHttpHost(
                value.GetString()!,
                allowProtocolRelative: false,
                out var host,
                out _)
            && template.IsLinkDomainAllowed(host);

    private static bool TryCanonicalHttpHost(
        string value,
        bool allowProtocolRelative,
        out string host,
        out string userInfo)
    {
        host = UnresolvedHost;
        userInfo = string.Empty;
        var protocolRelative = value.StartsWith("//", StringComparison.Ordinal);
        if (protocolRelative && !allowProtocolRelative)
        {
            return false;
        }

        var absolute = protocolRelative
            ? "https:" + value
            : value;

        try
        {
            if (!Uri.TryCreate(absolute, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return false;
            }

            // IdnHost is the comparison form: Unicode DNS names become their
            // ASCII punycode equivalent and IPv6 literals lose their brackets.
            // Neither can then disappear from an ASCII-only detector or be
            // compared under a different spelling than the metadata.
            var canonical = uri.IdnHost;
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return false;
            }

            host = canonical;
            userInfo = uri.UserInfo;
            return true;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static bool HasPlausibleSuffix(string host)
    {
        var lastDot = host.LastIndexOf('.');
        return lastDot > 0
            && lastDot < host.Length - 1
            && PlausibleSuffixes.Contains(host[(lastDot + 1)..]);
    }

    private static bool TryNormalizeMarkupUriReferences(string markup, out string normalized)
    {
        if (!TryNormalizeUriAttributeReferences(markup, out var withAttributes))
        {
            normalized = string.Empty;
            return false;
        }

        if (!TryNormalizeCssCarrierReferences(withAttributes, out var withCssCarriers))
        {
            normalized = string.Empty;
            return false;
        }

        return TryNormalizeMetaRefreshReferences(withCssCarriers, out normalized);
    }

    private static bool TryNormalizeUriAttributeReferences(string markup, out string normalized)
    {
        MatchCollection matches;
        try
        {
            matches = UriBearingAttribute().Matches(markup);
        }
        catch (RegexMatchTimeoutException)
        {
            normalized = string.Empty;
            return false;
        }

        if (matches.Count == 0)
        {
            normalized = markup;
            return true;
        }

        var output = new StringBuilder(markup.Length);
        var markupIndex = 0;
        foreach (Match match in matches)
        {
            Group destination = match.Groups["destination"];
            if (!TryNormalizeUriNamedReferences(destination.Value, out var normalizedDestination))
            {
                normalized = string.Empty;
                return false;
            }

            output.Append(markup, markupIndex, destination.Index - markupIndex);
            output.Append(normalizedDestination);
            markupIndex = destination.Index + destination.Length;
        }

        output.Append(markup, markupIndex, markup.Length - markupIndex);
        normalized = output.ToString();
        return true;
    }

    private static bool TryNormalizeCssCarrierReferences(string markup, out string normalized)
    {
        CssUrlScanResult scan = ScanCssUrlDestinations(markup);
        if (scan.IsMalformed)
        {
            normalized = string.Empty;
            return false;
        }

        if (scan.Carriers.Count == 0)
        {
            normalized = markup;
            return true;
        }

        var output = new StringBuilder(markup.Length);
        var markupIndex = 0;
        foreach (CssUrlCarrier carrier in scan.Carriers)
        {
            var carrierText = markup.Substring(carrier.Start, carrier.End - carrier.Start);
            if (!TryNormalizeUriNamedReferences(carrierText, out var normalizedCarrier))
            {
                normalized = string.Empty;
                return false;
            }

            output.Append(markup, markupIndex, carrier.Start - markupIndex);
            output.Append(normalizedCarrier);
            markupIndex = carrier.End;
        }

        output.Append(markup, markupIndex, markup.Length - markupIndex);
        normalized = output.ToString();
        return true;
    }

    private static bool TryNormalizeMetaRefreshReferences(string markup, out string normalized)
    {
        MetaRefreshScanResult scan = ScanMetaRefreshDestinations(markup);
        if (scan.IsMalformed)
        {
            normalized = string.Empty;
            return false;
        }

        if (scan.Carriers.Count == 0)
        {
            normalized = markup;
            return true;
        }

        var output = new StringBuilder(markup.Length);
        var markupIndex = 0;
        foreach (MetaRefreshCarrier carrier in scan.Carriers)
        {
            if (!TryNormalizeUriNamedReferences(carrier.Destination, out var normalizedDestination))
            {
                normalized = string.Empty;
                return false;
            }

            output.Append(markup, markupIndex, carrier.DestinationStart - markupIndex);
            output.Append(normalizedDestination);
            markupIndex = carrier.DestinationEnd;
        }

        output.Append(markup, markupIndex, markup.Length - markupIndex);
        normalized = output.ToString();
        return true;
    }

    private static bool TryNormalizeUriNamedReferences(string value, out string normalized)
    {
        StringBuilder? output = null;
        var copiedThrough = 0;

        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '&'
                || !TryFindNamedReference(value, index, out var semicolon))
            {
                continue;
            }

            var name = value.Substring(index + 1, semicolon - index - 1);
            if (Html5UriPunctuation.TryGetValue(name, out var punctuation))
            {
                output ??= new StringBuilder(value.Length);
                output.Append(value, copiedThrough, index - copiedThrough);
                output.Append(punctuation);
                copiedThrough = semicolon + 1;
                index = semicolon;
                continue;
            }

            var reference = value.Substring(index, semicolon - index + 1);
            if (string.Equals(WebUtility.HtmlDecode(reference), reference, StringComparison.Ordinal))
            {
                normalized = string.Empty;
                return false;
            }

            // Leave references understood by the platform decoder in their
            // encoded form. The single decode after this pass then preserves
            // the browser's non-recursive character-reference semantics.
            index = semicolon;
        }

        if (output is null)
        {
            normalized = value;
            return true;
        }

        output.Append(value, copiedThrough, value.Length - copiedThrough);
        normalized = output.ToString();
        return true;
    }

    private static bool TryFindNamedReference(string value, int ampersand, out int semicolon)
    {
        semicolon = -1;
        var nameStart = ampersand + 1;
        if (nameStart >= value.Length || !char.IsAsciiLetter(value[nameStart]))
        {
            return false;
        }

        var limit = Math.Min(value.Length, nameStart + 32);
        for (var index = nameStart + 1; index < limit; index++)
        {
            if (value[index] == ';')
            {
                semicolon = index;
                return true;
            }

            if (!char.IsAsciiLetterOrDigit(value[index]))
            {
                return false;
            }
        }

        return false;
    }

    private static MetaRefreshScanResult ScanMetaRefreshDestinations(string markup)
    {
        List<MetaRefreshCarrier> carriers = [];
        var destinationLength = 0;

        for (var index = 0; index < markup.Length; index++)
        {
            if (index + 4 <= markup.Length
                && markup.AsSpan(index).StartsWith("<!--", StringComparison.Ordinal))
            {
                var commentEnd = markup.IndexOf("-->", index + 4, StringComparison.Ordinal);
                if (commentEnd < 0)
                {
                    break;
                }

                index = commentEnd + 2;
                continue;
            }

            if (!IsMetaTagStart(markup, index))
            {
                continue;
            }

            if (!TryFindTagEnd(markup, index + 5, out var attributesEnd, out var tagEnd)
                || !TryReadMetaAttributes(
                    markup,
                    index + 5,
                    attributesEnd,
                    out HtmlAttributeValue? httpEquiv,
                    out HtmlAttributeValue? content))
            {
                return new MetaRefreshScanResult(carriers, destinationLength, IsMalformed: true);
            }

            index = tagEnd - 1;
            if (httpEquiv is not { } equiv
                || !string.Equals(
                    WebUtility.HtmlDecode(equiv.Value).Trim(),
                    "refresh",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (content is not { } refreshContent
                || !TryReadMetaRefreshDestination(
                    markup,
                    refreshContent,
                    out MetaRefreshCarrier carrier))
            {
                return new MetaRefreshScanResult(carriers, destinationLength, IsMalformed: true);
            }

            carriers.Add(carrier);
            destinationLength += carrier.Destination.Length + 1;
        }

        return new MetaRefreshScanResult(carriers, destinationLength, IsMalformed: false);
    }

    private static bool IsMetaTagStart(string markup, int index)
    {
        const int PrefixLength = 5;
        if (index + PrefixLength > markup.Length
            || markup[index] != '<'
            || !markup.AsSpan(index + 1, 4).Equals("meta", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return index + PrefixLength == markup.Length
            || IsCssWhitespace(markup[index + PrefixLength])
            || markup[index + PrefixLength] is '/' or '>';
    }

    private static bool TryFindTagEnd(
        string markup,
        int attributesStart,
        out int attributesEnd,
        out int tagEnd)
    {
        attributesEnd = -1;
        tagEnd = -1;
        var quote = '\0';
        for (var index = attributesStart; index < markup.Length; index++)
        {
            var character = markup[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                continue;
            }

            if (character == '>')
            {
                attributesEnd = index;
                tagEnd = index + 1;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadMetaAttributes(
        string markup,
        int attributesStart,
        int attributesEnd,
        out HtmlAttributeValue? httpEquiv,
        out HtmlAttributeValue? content)
    {
        httpEquiv = null;
        content = null;
        var index = attributesStart;
        while (index < attributesEnd)
        {
            while (index < attributesEnd && IsCssWhitespace(markup[index]))
            {
                index++;
            }

            if (index >= attributesEnd)
            {
                break;
            }

            if (markup[index] == '/')
            {
                index++;
                continue;
            }

            var nameStart = index;
            while (index < attributesEnd
                && !IsCssWhitespace(markup[index])
                && markup[index] is not '=' and not '/')
            {
                index++;
            }

            if (index == nameStart)
            {
                return false;
            }

            var nameLength = index - nameStart;
            while (index < attributesEnd && IsCssWhitespace(markup[index]))
            {
                index++;
            }

            HtmlAttributeValue value = new(index, index, string.Empty);
            if (index < attributesEnd && markup[index] == '=')
            {
                index++;
                while (index < attributesEnd && IsCssWhitespace(markup[index]))
                {
                    index++;
                }

                if (index >= attributesEnd)
                {
                    return false;
                }

                var quote = markup[index] is '\'' or '"' ? markup[index++] : '\0';
                var valueStart = index;
                if (quote == '\0')
                {
                    while (index < attributesEnd && !IsCssWhitespace(markup[index]))
                    {
                        index++;
                    }
                }
                else
                {
                    while (index < attributesEnd && markup[index] != quote)
                    {
                        index++;
                    }

                    if (index >= attributesEnd)
                    {
                        return false;
                    }
                }

                value = new HtmlAttributeValue(
                    valueStart,
                    index,
                    markup.Substring(valueStart, index - valueStart));
                if (quote != '\0')
                {
                    index++;
                }
            }

            ReadOnlySpan<char> name = markup.AsSpan(nameStart, nameLength);
            if (httpEquiv is null && name.Equals("http-equiv", StringComparison.OrdinalIgnoreCase))
            {
                httpEquiv = value;
            }
            else if (content is null && name.Equals("content", StringComparison.OrdinalIgnoreCase))
            {
                content = value;
            }
        }

        return true;
    }

    private static bool TryReadMetaRefreshDestination(
        string markup,
        HtmlAttributeValue content,
        out MetaRefreshCarrier carrier)
    {
        carrier = default;
        var index = content.Start;
        var end = content.End;
        while (index < end && IsCssWhitespace(markup[index]))
        {
            index++;
        }

        var delayStart = index;
        while (index < end && char.IsAsciiDigit(markup[index]))
        {
            index++;
        }

        if (index == delayStart)
        {
            return false;
        }

        while (index < end && IsCssWhitespace(markup[index]))
        {
            index++;
        }

        if (index >= end || markup[index++] != ';')
        {
            return false;
        }

        while (index < end && IsCssWhitespace(markup[index]))
        {
            index++;
        }

        if (index + 3 > end
            || !markup.AsSpan(index, 3).Equals("url", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        index += 3;
        while (index < end && IsCssWhitespace(markup[index]))
        {
            index++;
        }

        if (index >= end || markup[index++] != '=')
        {
            return false;
        }

        while (index < end && IsCssWhitespace(markup[index]))
        {
            index++;
        }

        while (end > index && IsCssWhitespace(markup[end - 1]))
        {
            end--;
        }

        if (index >= end)
        {
            return false;
        }

        if (markup[index] is '\'' or '"')
        {
            var quote = markup[index++];
            if (index >= end || markup[end - 1] != quote)
            {
                return false;
            }

            end--;
        }

        if (index >= end)
        {
            return false;
        }

        carrier = new MetaRefreshCarrier(
            index,
            end,
            markup.Substring(index, end - index));
        return true;
    }

    private static CssUrlScanResult ScanCssUrlDestinations(string markup)
    {
        List<CssUrlCarrier> carriers = [];
        var decodedLength = 0;

        for (var index = 0; index < markup.Length; index++)
        {
            if (markup[index] == '/'
                && index + 1 < markup.Length
                && markup[index + 1] == '*')
            {
                var commentEnd = markup.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (commentEnd < 0)
                {
                    break;
                }

                index = commentEnd + 1;
                continue;
            }

            if (!TryReadCssUrlFunction(markup, index, out var openParenthesis))
            {
                continue;
            }

            if (!TryReadCssUrl(
                markup,
                openParenthesis,
                out var destination,
                out var nextIndex))
            {
                return new CssUrlScanResult(carriers, decodedLength, IsMalformed: true);
            }

            carriers.Add(new CssUrlCarrier(index, nextIndex, destination));
            decodedLength += destination.Length + 1;
            index = nextIndex - 1;
        }

        return new CssUrlScanResult(carriers, decodedLength, IsMalformed: false);
    }

    private static bool TryReadCssUrlFunction(string markup, int start, out int openParenthesis)
    {
        openParenthesis = -1;
        if ((start > 0 && IsCssIdentifierCharacter(markup[start - 1]))
            || (!IsCssIdentifierCharacter(markup[start]) && markup[start] != '\\'))
        {
            return false;
        }

        var decoded = new StringBuilder(3);
        var index = start;
        while (index < markup.Length)
        {
            var character = markup[index];
            if (IsCssIdentifierCharacter(character))
            {
                decoded.Append(character);
                index++;
            }
            else if (character == '\\')
            {
                if (!TryAppendCssEscape(markup, ref index, decoded))
                {
                    return false;
                }
            }
            else
            {
                break;
            }

            if (decoded.Length > 3)
            {
                return false;
            }
        }

        if (!decoded.ToString().Equals("url", StringComparison.OrdinalIgnoreCase)
            || index >= markup.Length
            || markup[index] != '(')
        {
            return false;
        }

        openParenthesis = index;
        return true;
    }

    private static bool TryReadCssUrl(
        string markup,
        int openParenthesis,
        out string destination,
        out int nextIndex)
    {
        destination = string.Empty;
        nextIndex = openParenthesis + 1;

        while (nextIndex < markup.Length && IsCssWhitespace(markup[nextIndex]))
        {
            nextIndex++;
        }

        var quote = nextIndex < markup.Length && markup[nextIndex] is '\'' or '"'
            ? markup[nextIndex++]
            : '\0';
        var decoded = new StringBuilder();

        while (nextIndex < markup.Length)
        {
            var character = markup[nextIndex];
            if (character == '{'
                && nextIndex + 1 < markup.Length
                && markup[nextIndex + 1] == '{')
            {
                var expressionEnd = markup.IndexOf("}}", nextIndex + 2, StringComparison.Ordinal);
                if (expressionEnd < 0)
                {
                    return false;
                }

                var expressionLength = expressionEnd + 2 - nextIndex;
                decoded.Append(markup, nextIndex, expressionLength);
                nextIndex += expressionLength;
                continue;
            }

            if (quote != '\0')
            {
                if (character == quote)
                {
                    nextIndex++;
                    while (nextIndex < markup.Length && IsCssWhitespace(markup[nextIndex]))
                    {
                        nextIndex++;
                    }

                    if (nextIndex >= markup.Length || markup[nextIndex] != ')')
                    {
                        return false;
                    }

                    nextIndex++;
                    destination = decoded.ToString();
                    return true;
                }

                if (IsCssNewLine(character))
                {
                    return false;
                }
            }
            else
            {
                if (character == ')')
                {
                    nextIndex++;
                    destination = decoded.ToString();
                    return true;
                }

                if (IsCssWhitespace(character))
                {
                    while (nextIndex < markup.Length && IsCssWhitespace(markup[nextIndex]))
                    {
                        nextIndex++;
                    }

                    if (nextIndex >= markup.Length || markup[nextIndex] != ')')
                    {
                        return false;
                    }

                    nextIndex++;
                    destination = decoded.ToString();
                    return true;
                }

                if (character is '\'' or '"' or '(' || char.IsControl(character))
                {
                    return false;
                }
            }

            if (character == '\\')
            {
                if (!TryAppendCssEscape(markup, ref nextIndex, decoded))
                {
                    return false;
                }

                continue;
            }

            decoded.Append(character);
            nextIndex++;
        }

        return false;
    }

    private static bool TryAppendCssEscape(string markup, ref int index, StringBuilder decoded)
    {
        index++;
        if (index >= markup.Length || IsCssNewLine(markup[index]))
        {
            return false;
        }

        if (!IsCssHexDigit(markup[index]))
        {
            decoded.Append(markup[index]);
            index++;
            return true;
        }

        var scalar = 0;
        var digits = 0;
        while (index < markup.Length && digits < 6 && IsCssHexDigit(markup[index]))
        {
            scalar = (scalar * 16) + CssHexValue(markup[index]);
            index++;
            digits++;
        }

        if (index < markup.Length && IsCssWhitespace(markup[index]))
        {
            if (markup[index] == '\r' && index + 1 < markup.Length && markup[index + 1] == '\n')
            {
                index += 2;
            }
            else
            {
                index++;
            }
        }

        if (scalar == 0 || scalar > 0x10FFFF || scalar is >= 0xD800 and <= 0xDFFF)
        {
            return false;
        }

        decoded.Append(char.ConvertFromUtf32(scalar));
        return true;
    }

    private static bool IsCssIdentifierCharacter(char character)
        => character is '-' or '_'
            || character >= 0x80
            || char.IsAsciiLetterOrDigit(character);

    private static bool IsCssWhitespace(char character)
        => character is ' ' or '\t' or '\n' or '\r' or '\f';

    private static bool IsCssNewLine(char character)
        => character is '\n' or '\r' or '\f';

    private static bool IsCssHexDigit(char character)
        => character is >= '0' and <= '9'
            || character is >= 'a' and <= 'f'
            || character is >= 'A' and <= 'F';

    private static int CssHexValue(char character)
        => character <= '9'
            ? character - '0'
            : (character & 0xDF) - 'A' + 10;

    private readonly record struct CssUrlScanResult(
        IReadOnlyList<CssUrlCarrier> Carriers,
        int DecodedLength,
        bool IsMalformed);

    private readonly record struct CssUrlCarrier(int Start, int End, string Destination);

    private readonly record struct MetaRefreshScanResult(
        IReadOnlyList<MetaRefreshCarrier> Carriers,
        int DestinationLength,
        bool IsMalformed);

    private readonly record struct MetaRefreshCarrier(
        int DestinationStart,
        int DestinationEnd,
        string Destination);

    private readonly record struct HtmlAttributeValue(int Start, int End, string Value);

    // Announced and protocol-relative destinations are delimited here, but
    // their authority is interpreted by System.Uri above. The expression does
    // not try to encode DNS, IDN, IP-literal, userinfo, or port grammar: doing
    // so is what made a syntactically valid destination invisible whenever its
    // host was not an ASCII label.
    //
    // The bare-host alternative stays deliberately narrow. Unlike an announced
    // destination, it has no URI syntax to distinguish a domain from Brazilian
    // documents, invoice numbers, or clauses, so it retains the plausible-TLD
    // gate above.
    [GeneratedRegex(
        @"(?<announced>(?:https?://|//)(?<authority>[^\s/?#<>""']*))"
        + @"|\b(?<host>[a-z0-9][a-z0-9\-]*(?:\.[a-z0-9\-]+)*\.[a-z]{2,24})(?:[/:?#]|\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex LinkCandidate();

    /// <summary>Bare hosts inside userinfo, with the same narrow grammar as ordinary text.</summary>
    [GeneratedRegex(
        @"\b(?<host>[a-z0-9][a-z0-9\-]*(?:\.[a-z0-9\-]+)*\.[a-z]{2,24})(?:[/:?#]|\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex BareHostCandidate();

    /// <summary>HTML attributes whose values browsers interpret as URI-bearing data.</summary>
    [GeneratedRegex(
        @"\b(?:href|src|action|formaction|poster|cite|background|data|longdesc|manifest|ping|srcset)\s*=\s*"
        + @"(?:""(?<destination>[^""]*)""|'(?<destination>[^']*)'|(?<destination>[^\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex UriBearingAttribute();

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
