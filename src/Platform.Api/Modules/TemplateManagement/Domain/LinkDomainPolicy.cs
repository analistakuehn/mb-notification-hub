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

    // The three characters a URL parser removes from anywhere in the string it
    // parses, before it reads the scheme or the authority. Every other space or
    // control the specification names is stripped from the ends only, which is
    // what UrlTrimmedCharacters below covers.
    private const char Tab = '\t';
    private const char LineFeed = '\n';
    private const char CarriageReturn = '\r';

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

    /// <summary>
    /// The C0 controls and the space, which a URL parser strips from both ends
    /// of the value before parsing it.
    /// </summary>
    private static readonly char[] UrlTrimmedCharacters =
        [.. Enumerable.Range(0, 0x21).Select(code => (char)code)];

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
                if (match.Groups["authority"].Success)
                {
                    Group scheme = match.Groups["scheme"];
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

                    var canonical = TryCanonicalHttpHost(
                        CanonicalSeparator(match),
                        allowProtocolRelative: true,
                        out var host,
                        out var userInfo);

                    // A destination whose separator carries nothing is spelled
                    // exactly like ordinary writing, so it has to earn the
                    // reading before it gets one.
                    var spelledLikeProse = !scheme.Success
                        || match.Length == scheme.Length + authority.Length;
                    if (spelledLikeProse && !IsAddressRatherThanProse(scheme.Value, authority))
                    {
                        continue;
                    }

                    if (!canonical)
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
    /// The candidate rewritten with the two slashes the canonizer expects, so
    /// the run of slashes and backslashes the detector accepted reaches
    /// <see cref="System.Uri"/> in the one form it parses. The rewrite covers
    /// the separator and nothing else: a backslash later in the authority is
    /// left where the author wrote it, and the destination that carries one
    /// keeps failing closed instead of being reinterpreted into a host.
    /// </summary>
    /// <summary>
    /// Whether a destination written without the two slashes that announce an
    /// authority is an address at all.
    /// <para>
    /// Two spellings reach a host with no separator to mark them: a scheme
    /// glued straight to its authority, and an authority with no scheme in
    /// front of it. Both are also how ordinary writing looks. Measured over
    /// Portuguese operational text, reading every one of them as a destination
    /// refused a note that says <c>codigo HTTP:200</c>, <c>HTTPS:443</c>,
    /// <c>http:port nao configurado</c>, <c>Https:Sim</c>, <c>campo
    /// https:true</c>, and a Windows share path, which is the vocabulary of the
    /// notes an operator writes about configuration.
    /// </para>
    /// <para>
    /// A dot or a colon in the authority is what separates the two: every
    /// public name and every IP literal carries one, and none of those notes
    /// does. The dot has to sit inside the part a parser would read as the
    /// authority, which is everything before the first backslash: a trailing
    /// period ends a sentence and a dot after a backslash belongs to a file
    /// name, and neither one names a host. The exception is the address written as a single number, which is
    /// why a bare word still counts when the canonizer turns it into a dotted
    /// address outside <c>0.0.0.0/8</c>. That keeps <c>https:3232235777</c>,
    /// which is 192.168.1.1 spelled without dots, and drops <c>HTTP:200</c>,
    /// which is 0.0.0.200, an address in the block reserved for "this network"
    /// that no client routes to. What the rule gives up is named: a dotless
    /// intranet name such as <c>https:relatorios</c> stops being read as a
    /// destination.
    /// </para>
    /// </summary>
    private static bool IsAddressRatherThanProse(string scheme, string authority)
    {
        // A URL parser ends an authority at the first backslash, so only what
        // stands in front of one can name a host. Reading the whole run instead
        // borrowed the dot of a file extension and turned a Windows share path
        // into a destination.
        var backslash = authority.IndexOf('\\');
        var named = backslash < 0
            ? authority
            : authority[..backslash];

        var dot = named.IndexOf('.', StringComparison.Ordinal);
        if ((dot > 0 && dot < named.Length - 1)
            || named.Contains(':', StringComparison.Ordinal))
        {
            return true;
        }

        // The canonizer is asked about the same text the dot was looked for in,
        // and a refusal here answers "address", never "prose". Silence is the
        // one thing this gate must not produce: a value the canonizer cannot
        // read is an unreadable destination, which already has an answer, and
        // treating it as writing turned every percent-encoded dot into an
        // approval with no host to show for it.
        if (!TryCanonicalHttpHost(
            scheme + "//" + named,
            allowProtocolRelative: true,
            out var host,
            out _))
        {
            return true;
        }

        var hostDot = host.IndexOf('.', StringComparison.Ordinal);
        return hostDot > 0
            && hostDot < host.Length - 1
            && !host.StartsWith("0.", StringComparison.Ordinal);
    }

    private static string CanonicalSeparator(Match match)
        => string.Concat(
            match.Groups["scheme"].ValueSpan,
            "//",
            match.Groups["authority"].ValueSpan);

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
        if (!TryReadAttributeDestinations(markup, out markup))
        {
            return MalformedDestination;
        }

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

    /// <summary>
    /// Attribute destinations in the form a client resolves them, and only
    /// those a client should be asked to resolve.
    /// <para>
    /// The tab, the line feed, and the carriage return are removed from the
    /// value before anything reads it, which is the step every URL parser
    /// performs on the string it is handed. The order is the whole point: the
    /// detector's authority stops at the first whitespace, so a value cleaned
    /// after the candidate has been cut is cleaned too late and the host the
    /// client would reach was already discarded. Decoded character references
    /// deliver those characters as readily as a literal one, which is why the
    /// removal sits after the single decode.
    /// </para>
    /// <para>
    /// A destination that declares a scheme this module does not deliver is
    /// refused here rather than measured against the allowed domains, because
    /// there is no host in it to measure. The allowlist is a statement about
    /// where a reader is sent, and a destination that carries its payload
    /// instead of naming a place answers that question by having no answer.
    /// </para>
    /// </summary>
    private static bool TryReadAttributeDestinations(string markup, out string normalized)
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

        StringBuilder? output = null;
        var markupIndex = 0;
        foreach (Match match in matches)
        {
            Group destination = match.Groups["destination"];
            var prepared = IsListValued(match.Groups["attribute"].ValueSpan)
                ? PreparedListDestination(destination.ValueSpan)
                : PreparedCandidate(destination.ValueSpan);
            if (prepared is null || destination.ValueSpan.SequenceEqual(prepared))
            {
                continue;
            }

            output ??= new StringBuilder(markup.Length);
            output.Append(markup, markupIndex, destination.Index - markupIndex);
            output.Append(prepared);
            markupIndex = destination.Index + destination.Length;
        }

        if (output is null)
        {
            normalized = markup;
            return true;
        }

        output.Append(markup, markupIndex, markup.Length - markupIndex);
        normalized = output.ToString();
        return true;
    }

    /// <summary>
    /// The destination in the form the scan can read whole, or null when it is
    /// already in that form.
    /// <para>
    /// A destination that names a scheme the catalog does not deliver keeps its
    /// text and gains the malformed marker beside it. Refusing the whole markup
    /// on sight was cheaper and said less: a value like
    /// <c>blob:https://elsewhere.example/1</c> carries the host an author has to
    /// read to fix the template, and answering only with the marker made the
    /// refusal indistinguishable from an unreadable one. The text is left
    /// unencoded there on purpose, because the marker already decides the
    /// verdict and the only remaining job is to name a host if the value holds
    /// one.
    /// </para>
    /// </summary>
    private static string? PreparedCandidate(ReadOnlySpan<char> destination)
    {
        var removed = false;
        var encoded = false;
        foreach (var character in destination)
        {
            if (character is Tab or LineFeed or CarriageReturn)
            {
                removed = true;
            }
            else if (!IsCarriedByCandidate(character))
            {
                encoded = true;
            }
        }

        if (!removed && !encoded && IsDeliverableDestination(destination))
        {
            return null;
        }

        var cleaned = removed
            ? WithoutUrlRemovedCharacters(destination)
            : destination.ToString();

        return IsDeliverableDestination(cleaned)
            ? WithUncarriedCharactersEncoded(cleaned)
            : cleaned + " " + MalformedDestination;
    }

    /// <summary>
    /// A value that holds several destinations, prepared one destination at a
    /// time.
    /// <para>
    /// srcset and ping separate their entries with ASCII whitespace and commas,
    /// and those separators are the boundary the scan reads a destination from.
    /// Percent-encoding one of them, which is right everywhere else because a
    /// space inside a single URL belongs to the userinfo, welded two entries
    /// into one and hid the second: the character in front of it stopped being
    /// a boundary. Preparing each entry on its own keeps both rules, the
    /// separator stays a separator and everything inside an entry is still
    /// handed to the canonizer whole.
    /// </para>
    /// </summary>
    private static string PreparedListDestination(ReadOnlySpan<char> destination)
    {
        var output = new StringBuilder(destination.Length + 8);
        var start = 0;
        for (var index = 0; index <= destination.Length; index++)
        {
            if (index < destination.Length && !IsListSeparator(destination[index]))
            {
                continue;
            }

            if (index > start)
            {
                ReadOnlySpan<char> candidate = destination[start..index];
                if (output.Length > 0)
                {
                    output.Append(' ');
                }

                output.Append(PreparedCandidate(candidate) ?? candidate.ToString());
            }

            start = index + 1;
        }

        return output.ToString();
    }

    /// <summary>
    /// The attributes whose value is a list of destinations rather than one.
    /// </summary>
    private static bool IsListValued(ReadOnlySpan<char> attribute)
        => attribute.Equals("srcset", StringComparison.OrdinalIgnoreCase)
            || attribute.Equals("ping", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ASCII whitespace and the comma, which is what the srcset and ping
    /// grammars split on. Nothing wider: a no-break space is not a separator in
    /// either grammar, so it stays inside the entry and reaches the canonizer.
    /// </summary>
    private static bool IsListSeparator(char character)
        => character is ',' or ' ' or Tab or LineFeed or CarriageReturn or '\f';

    /// <summary>
    /// Whether the candidate grammar can carry the character inside an
    /// authority. The answer is read off that grammar and not off a list of
    /// characters somebody remembered: the detector excludes whitespace and the
    /// four markup delimiters, and everything else it excludes is a real URL
    /// delimiter that has to keep delimiting.
    /// </summary>
    private static bool IsCarriedByCandidate(char character)
        => !char.IsWhiteSpace(character)
            && character is not ('<' or '>' or '"' or '\'');

    /// <summary>
    /// The destination with every character the candidate grammar cannot carry
    /// replaced by its percent-encoding.
    /// <para>
    /// This is the rule the whole guard turns on, and the one the two findings
    /// broke twice: the detector must never be stricter than the canonizer it
    /// feeds. A URL parser ends an authority at a slash, a backslash, a question
    /// mark, a number sign, or the end of the value, and at nothing else; it
    /// carries a quote, an angle bracket, and every kind of space straight into
    /// the userinfo and reads the host after the at sign. A detector that stops
    /// at those characters hands over a prefix, gets back the wrong host, and
    /// approves. Percent-encoding them is what the parser itself does with them,
    /// so the authority arrives whole and the decision stays with System.Uri.
    /// </para>
    /// </summary>
    private static string WithUncarriedCharactersEncoded(string value)
    {
        StringBuilder? output = null;
        var copiedThrough = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (IsCarriedByCandidate(value[index]))
            {
                continue;
            }

            output ??= new StringBuilder(value.Length + 8);
            output.Append(value, copiedThrough, index - copiedThrough);
            AppendPercentEncoded(output, value, ref index);
            copiedThrough = index + 1;
        }

        if (output is null)
        {
            return value;
        }

        output.Append(value, copiedThrough, value.Length - copiedThrough);
        return output.ToString();
    }

    private static void AppendPercentEncoded(StringBuilder output, string value, ref int index)
    {
        const string Hexadecimal = "0123456789ABCDEF";
        var length = char.IsHighSurrogate(value[index])
            && index + 1 < value.Length
            && char.IsLowSurrogate(value[index + 1])
                ? 2
                : 1;

        Span<byte> utf8 = stackalloc byte[4];
        var written = Encoding.UTF8.GetBytes(value.AsSpan(index, length), utf8);
        for (var position = 0; position < written; position++)
        {
            output.Append('%');
            output.Append(Hexadecimal[utf8[position] >> 4]);
            output.Append(Hexadecimal[utf8[position] & 0xF]);
        }

        index += length - 1;
    }

    private static string WithoutUrlRemovedCharacters(ReadOnlySpan<char> destination)
    {
        var kept = new StringBuilder(destination.Length);
        foreach (var character in destination)
        {
            if (character is not (Tab or LineFeed or CarriageReturn))
            {
                kept.Append(character);
            }
        }

        return kept.ToString();
    }

    /// <summary>
    /// Whether the destination names a place this module is willing to send a
    /// reader to. A value with no scheme is relative and stays: it resolves
    /// against the message it was delivered in, which is the sender's own
    /// document.
    /// <para>
    /// Five schemes are delivered. Two of them, http and https, name a place on
    /// the network and the allowed domains decide them. The other three name no
    /// place at all: mailto addresses a mailbox, tel a telephone number, and
    /// cid a part of the message the reader already holds, which is how an
    /// inline image is written in mail. Having no host is not the same failure
    /// as data and blob, which carry their payload instead of naming anything:
    /// no host here means no external destination, so the allowlist has nothing
    /// to rule on and nothing to protect. What is left of their value still
    /// goes through the same preparation and the same scan, so a literal
    /// address written inside one of them is read exactly as it would be
    /// anywhere else.
    /// </para>
    /// </summary>
    private static bool IsDeliverableDestination(ReadOnlySpan<char> destination)
    {
        ReadOnlySpan<char> value = destination.Trim(UrlTrimmedCharacters);
        var colon = -1;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == ':')
            {
                colon = index;
                break;
            }

            var isSchemeCharacter = index == 0
                ? char.IsAsciiLetter(value[index])
                : char.IsAsciiLetterOrDigit(value[index]) || value[index] is '+' or '-' or '.';
            if (!isSchemeCharacter)
            {
                // No scheme is declared before the value stops looking like
                // one, so this is a relative reference.
                return true;
            }
        }

        if (colon <= 0)
        {
            return true;
        }

        ReadOnlySpan<char> scheme = value[..colon];
        if (scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase)
            && !scheme.Equals("cid", StringComparison.OrdinalIgnoreCase)
            && !scheme.Equals("tel", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // None of these three names a place on the network, so none of them has
        // an authority to name one with: a Content-ID addresses a part of the
        // message the reader already has, and a telephone number and a mailbox
        // address no place at all. A value that opens an authority is therefore
        // not one of them whatever it wrote before the colon, and it is refused
        // rather than trusted to a client's reading of a scheme that has no
        // authority to read.
        ReadOnlySpan<char> opaque = value[(colon + 1)..];
        return opaque.Length < 2
            || !IsAuthoritySlash(opaque[0])
            || !IsAuthoritySlash(opaque[1]);
    }

    private static bool IsAuthoritySlash(char character)
        => character is '/' or '\\';

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
            output.Append(WithQuotesKeptInsideTheValue(normalizedDestination));
            markupIndex = destination.Index + destination.Length;
        }

        output.Append(markup, markupIndex, markup.Length - markupIndex);
        normalized = output.ToString();
        return true;
    }

    /// <summary>
    /// The value with the character references that spell an attribute quote
    /// replaced by their percent-encodings.
    /// <para>
    /// An HTML parser reads the attribute value first and resolves the
    /// references inside it afterwards, so a quote written as a reference is
    /// data. Decoding the whole document before the values are cut reverses
    /// that order and turns the data back into structure: the reference closes
    /// the attribute a scan later reads, and everything the author put after it,
    /// userinfo and host included, falls outside the destination. The two
    /// characters here are exactly the two the attribute grammar uses to
    /// delimit a value, and the percent-encoding is what a URL parser writes
    /// for them anyway.
    /// </para>
    /// </summary>
    private static string WithQuotesKeptInsideTheValue(string value)
    {
        if (!value.Contains('&', StringComparison.Ordinal))
        {
            return value;
        }

        StringBuilder? output = null;
        var copiedThrough = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '&'
                || !TryFindCharacterReference(value, index, out var semicolon))
            {
                continue;
            }

            var reference = value.Substring(index, semicolon - index + 1);
            var decoded = WebUtility.HtmlDecode(reference);
            if (decoded is not ("\"" or "'"))
            {
                index = semicolon;
                continue;
            }

            output ??= new StringBuilder(value.Length);
            output.Append(value, copiedThrough, index - copiedThrough);
            output.Append(decoded == "\"" ? "%22" : "%27");
            copiedThrough = semicolon + 1;
            index = semicolon;
        }

        if (output is null)
        {
            return value;
        }

        output.Append(value, copiedThrough, value.Length - copiedThrough);
        return output.ToString();
    }

    /// <summary>
    /// One character reference, named or numeric, ending at its semicolon.
    /// Wider than the named-only reader above, because a numeric reference
    /// spells the same character and has to be recognized in the same place.
    /// </summary>
    private static bool TryFindCharacterReference(string value, int ampersand, out int semicolon)
    {
        semicolon = -1;
        var start = ampersand + 1;
        if (start >= value.Length)
        {
            return false;
        }

        var index = start;
        if (value[index] == '#')
        {
            index++;
            if (index < value.Length && (value[index] is 'x' or 'X'))
            {
                index++;
            }
        }

        var body = index;
        var limit = Math.Min(value.Length, start + 32);
        for (; index < limit; index++)
        {
            if (value[index] == ';')
            {
                if (index == body)
                {
                    return false;
                }

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

        // The name that decides this is three characters long, so it is read
        // into the frame. The question is asked once per word start over the
        // whole body, and a builder and a string per question is what made the
        // scan allocate tens of megabytes on a body at the render ceiling.
        Span<char> decoded = stackalloc char[4];
        var length = 0;
        var index = start;
        while (index < markup.Length)
        {
            var character = markup[index];
            if (IsCssIdentifierCharacter(character))
            {
                decoded[Math.Min(length, decoded.Length - 1)] = character;
                length++;
                index++;
            }
            else if (character == '\\')
            {
                if (!TryReadCssEscape(markup, ref index, out var high, out var low, out var escaped))
                {
                    return false;
                }

                decoded[Math.Min(length, decoded.Length - 1)] = high;
                length++;
                if (escaped == 2)
                {
                    decoded[Math.Min(length, decoded.Length - 1)] = low;
                    length++;
                }
            }
            else
            {
                break;
            }

            if (length > 3)
            {
                return false;
            }
        }

        if (length != 3
            || !decoded[..3].Equals("url", StringComparison.OrdinalIgnoreCase)
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
        if (!TryReadCssEscape(markup, ref index, out var high, out var low, out var escaped))
        {
            return false;
        }

        decoded.Append(high);
        if (escaped == 2)
        {
            decoded.Append(low);
        }

        return true;
    }

    /// <summary>
    /// One CSS escape, decoded into its UTF-16 code units rather than into a
    /// string, so a caller that only needs to compare a short name pays no
    /// allocation for the comparison. <paramref name="escaped"/> is 1 or 2.
    /// </summary>
    private static bool TryReadCssEscape(
        string markup,
        ref int index,
        out char high,
        out char low,
        out int escaped)
    {
        high = '\0';
        low = '\0';
        escaped = 0;
        index++;
        if (index >= markup.Length || IsCssNewLine(markup[index]))
        {
            return false;
        }

        if (!IsCssHexDigit(markup[index]))
        {
            high = markup[index];
            escaped = 1;
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

        if (scalar <= 0xFFFF)
        {
            high = (char)scalar;
            escaped = 1;
            return true;
        }

        var supplementary = scalar - 0x10000;
        high = (char)(0xD800 + (supplementary >> 10));
        low = (char)(0xDC00 + (supplementary & 0x3FF));
        escaped = 2;
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
    // Announced and protocol-relative destinations are delimited here, but
    // their authority is interpreted by System.Uri above. The expression does
    // not try to encode DNS, IDN, IP-literal, userinfo, or port grammar: doing
    // so is what made a syntactically valid destination invisible whenever its
    // host was not an ASCII label.
    //
    // The separator between the scheme and the authority is any run of slashes
    // and backslashes, empty included, because that is what a client accepts:
    // after a special scheme a URL parser skips every slash and backslash it
    // finds and starts the authority at the next character. The alternative
    // with no scheme skips the same run for the same reason: a client that
    // reads two of them stops counting and consumes the rest. Demanding the two
    // canonical slashes made the detector stricter than the canonizer it feeds,
    // and a destination the canonizer resolves but the detector never sees is
    // an approval, not a refusal.
    //
    // The width is paid for in false positives, and they are not rare. A scheme
    // glued to a token reads as an address whatever the token is, and the
    // measured shapes are the ordinary vocabulary of a note about
    // configuration: "código HTTP:200", "HTTPS:443", "erro HTTP:404",
    // "status http:500", "http:port nao configurado", "Https:Sim", "campo
    // https:true", plus a Windows share path and an arithmetic ratio for the
    // alternative below. None of them is refused, and none of the shapes that
    // matter is let through: HostsIn answers with the gate in
    // IsAddressRatherThanProse rather than with a narrower expression here,
    // because no expression tells 200 from 3232235777, which is 192.168.1.1
    // spelled without a dot.
    //
    // The alternative with no scheme answers only at a value boundary. A run
    // of slashes in the middle of a value is a path a doubled separator
    // produced, not a second destination, and reading one as a host stopped a
    // template that already publishes from rendering at all.
    //
    // The bare-host alternative stays deliberately narrow. Unlike an announced
    // destination, it has no URI syntax to distinguish a domain from Brazilian
    // documents, invoice numbers, or clauses, so it retains the plausible-TLD
    // gate above.
    [GeneratedRegex(
        @"(?<scheme>https?:)[/\\]*(?<authority>[^\s/?#<>""']*)"
        + @"|(?:\A|[\s""'=(\[>])[/\\]{2,}(?<authority>[^\s/?#<>""']*)"
        + @"|\b(?<host>[a-z0-9][a-z0-9\-]*(?:\.[a-z0-9\-]+)*\.[a-z]{2,24})(?:[/:?#]|\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex LinkCandidate();

    /// <summary>Bare hosts inside userinfo, with the same narrow grammar as ordinary text.</summary>
    [GeneratedRegex(
        @"\b(?<host>[a-z0-9][a-z0-9\-]*(?:\.[a-z0-9\-]+)*\.[a-z]{2,24})(?:[/:?#]|\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex BareHostCandidate();

    /// <summary>HTML attributes whose values browsers interpret as URI-bearing data.</summary>
    [GeneratedRegex(
        @"\b(?<attribute>href|src|action|formaction|poster|cite|background|data|longdesc|manifest|ping|srcset)\s*=\s*"
        + @"(?:""(?<destination>[^""]*)""|'(?<destination>[^']*)'|(?<destination>[^\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex UriBearingAttribute();

    // The DOCTYPE runs to the first ">", which is where every standard
    // declaration ends: the public identifier and the DTD location it carries
    // are quoted and hold no ">" of their own.
    [GeneratedRegex(
        @"<!doctype[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex DocumentTypeDeclaration();

    // Both quoting styles in one alternation, because an attribute value is
    // delimited by the quote that opened it and nothing else may close it.
    [GeneratedRegex(
        @"\bxmlns(?::[a-z0-9_.-]+)?\s*=\s*""[^""]*"""
        + @"|\bxmlns(?::[a-z0-9_.-]+)?\s*=\s*'[^']*'",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex XmlNamespaceDeclaration();
}
