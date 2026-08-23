using System.Globalization;
using System.Text.RegularExpressions;

namespace NotificationHub.Api.Modules.Audit.Domain;

/// <summary>
/// Deterministic object keys of the WORM export. Every key is a pure function
/// of the prefix, the table, the partition, and the export window, so a rerun
/// addresses exactly the objects the first run wrote (which is what makes the
/// export idempotent), and an auditor navigates the bucket without an index.
/// </summary>
internal static partial class AuditExportKeys
{
    /// <summary>Chain segment, one canonical event per line, gzip compressed.</summary>
    internal const string EventsObject = "events.ndjson.gz";

    /// <summary>Rows that predate the chain, canonicalized at export time, gzip compressed.</summary>
    internal const string UnchainedObject = "unchained.ndjson.gz";

    internal const string ManifestObject = "manifest.json";

    internal const string AttestationObject = "attestation.json";

    internal const string DailyExport = "daily";

    internal const string ClosingExport = "closing";

    /// <summary>Folder of the daily export of one partition.</summary>
    internal static string DailyFolder(string prefix, string table, string partition, DateOnly day)
        => $"{PartitionFolder(prefix, table, partition)}{DailyExport}/{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}/";

    /// <summary>Folder of the authoritative closing export of one partition.</summary>
    internal static string ClosingFolder(string prefix, string table, string partition)
        => $"{PartitionFolder(prefix, table, partition)}{ClosingExport}/";

    /// <summary>
    /// Archive of the public half of a signing key. It lives in the same
    /// bucket as the evidence on purpose: verification must never depend on
    /// the key provider still existing.
    /// </summary>
    internal static string PublicKeyObject(string prefix, string keyId)
        => $"{Normalize(prefix)}attestation-keys/{SanitizeKeyId(keyId)}.json";

    /// <summary>Prefix with exactly one trailing separator, whatever the configuration wrote.</summary>
    internal static string Normalize(string prefix)
    {
        var trimmed = prefix.Trim().Trim('/');
        return trimmed.Length == 0 ? string.Empty : trimmed + "/";
    }

    /// <summary>
    /// A key id may be an ARN, which carries separators that would turn one
    /// object into a folder tree; anything outside the safe set becomes an
    /// underscore.
    /// </summary>
    internal static string SanitizeKeyId(string keyId)
        => UnsafeKeyIdCharacter().Replace(keyId, "_");

    private static string PartitionFolder(string prefix, string table, string partition)
        => $"{Normalize(prefix)}{table}/{partition}/";

    [GeneratedRegex("[^A-Za-z0-9._-]")]
    private static partial Regex UnsafeKeyIdCharacter();
}
