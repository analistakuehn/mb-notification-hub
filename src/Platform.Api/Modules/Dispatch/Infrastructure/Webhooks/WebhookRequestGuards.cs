using System.Globalization;

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
    /// Tells whether the origin is allowed. An empty allowlist means the
    /// allowlist is off, which is the shipped posture: the network edge is
    /// the primary place to pin provider ranges, and a half-configured list
    /// inside the application would silently drop real callbacks. A
    /// configured list with no known origin is a refusal, never a pass.
    /// </summary>
    internal static bool IsOriginAllowed(string? remoteIpAddress, IReadOnlyList<string> allowedPrefixes)
    {
        if (allowedPrefixes.Count == 0) return true;

        if (string.IsNullOrWhiteSpace(remoteIpAddress)) return false;

        foreach (var prefix in allowedPrefixes)
        {
            if (prefix.Length > 0
                && remoteIpAddress.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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
