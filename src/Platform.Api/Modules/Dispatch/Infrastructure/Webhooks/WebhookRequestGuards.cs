using System.Globalization;
using System.Net;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Webhooks;

/// <summary>
/// The checks every provider callback passes before its signature is even
/// computed, shared so the two adapters cannot drift into two security
/// postures. Header lookup, origin allowlisting and the replay window are
/// identical work; only the header names and the signed payload differ.
/// </summary>
internal static class WebhookRequestGuards
{
    /// <summary>
    /// Reads a header without relying on the comparer of the dictionary the
    /// caller built. HTTP header names are case-insensitive, and a caller
    /// that hands over an ordinal dictionary would otherwise silently turn a
    /// signed request into an unsigned one.
    /// </summary>
    internal static string? FindHeader(IReadOnlyDictionary<string, string> headers, string name)
    {
        if (headers.TryGetValue(name, out var direct)) return direct;

        foreach (KeyValuePair<string, string> header in headers)
            if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)) return header.Value;

        return null;
    }

    /// <summary>
    /// Tells whether the origin falls inside one of the allowed networks. An
    /// empty allowlist means the allowlist is off, which is the shipped
    /// posture: pinning provider ranges belongs to the network edge, where the
    /// address of the client is the one the edge actually sees. This guard is
    /// defence in depth for a directly exposed host and nothing more.
    /// <para>
    /// The comparison is by network and not by text, because a textual prefix
    /// is not a range: <c>54.172.6</c> reads as a prefix of <c>54.172.60.x</c>
    /// through <c>54.172.69.x</c>, quietly authorising nine networks nobody
    /// listed, and an address written in another equally valid form never
    /// matches at all. An address the runtime cannot parse is a refusal, and a
    /// configured list with no matching network is a refusal, never a pass.
    /// </para>
    /// <para>
    /// An IPv4 address arriving in its mapped IPv6 form is compared as IPv4,
    /// because the two spellings name the same host and a list written in the
    /// obvious form would otherwise refuse every callback on a dual stack
    /// listener.
    /// </para>
    /// </summary>
    internal static bool IsOriginAllowed(string? remoteIpAddress, IReadOnlyList<IPNetwork> allowedNetworks)
    {
        if (allowedNetworks.Count == 0) return true;

        if (!IPAddress.TryParse(remoteIpAddress, out IPAddress? origin)) return false;

        if (origin.IsIPv4MappedToIPv6) origin = origin.MapToIPv4();

        foreach (IPNetwork network in allowedNetworks)
            if (network.Contains(origin)) return true;

        return false;
    }

    /// <summary>
    /// Parses configured networks in CIDR form, naming the first value that is
    /// not one. Callers validate at startup rather than at verification time:
    /// a range nobody can parse is a configuration error, and discovering it
    /// on the first real callback would either refuse authentic traffic or
    /// silently widen the allowlist, depending on which way the parse failed.
    /// </summary>
    internal static bool TryParseNetworks(
        IReadOnlyList<string> values,
        out IPNetwork[] networks,
        out string? invalidValue)
    {
        var parsed = new List<IPNetwork>(values.Count);
        foreach (var value in values)
        {
            if (!IPNetwork.TryParse(value, out IPNetwork network))
            {
                networks = [];
                invalidValue = value;
                return false;
            }

            parsed.Add(network);
        }

        networks = [.. parsed];
        invalidValue = null;
        return true;
    }

    /// <summary>
    /// Reads a Unix-second timestamp and tells whether it falls inside the
    /// replay window. The window is symmetric because clock skew runs both
    /// ways, and an unreadable value counts as outside the window: a
    /// timestamp that cannot be placed in time cannot prove freshness.
    /// </summary>
    internal static bool IsWithinWindow(
        string? unixSeconds,
        DateTimeOffset now,
        int windowSeconds,
        out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (!long.TryParse(unixSeconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            || seconds is < 0 or > 253_402_300_799)
        {
            return false;
        }

        timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return Math.Abs((now - timestamp).TotalSeconds) <= windowSeconds;
    }

    /// <summary>
    /// Tells whether a configured vocabulary names a provider term. Provider
    /// vocabularies are compared without regard to case because providers are
    /// inconsistent about it between their documentation and their payloads.
    /// </summary>
    internal static bool Names(IReadOnlyList<string> values, string candidate)
    {
        foreach (var value in values)
            if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>
    /// Decodes Base64 without exceptions, because the input is attacker
    /// controlled and a malformed value is an ordinary refusal, not a fault.
    /// </summary>
    internal static byte[]? TryDecodeBase64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var buffer = new byte[(value.Length / 4 * 3) + 3];
        return Convert.TryFromBase64String(value, buffer, out var written)
            ? buffer[..written]
            : null;
    }
}
