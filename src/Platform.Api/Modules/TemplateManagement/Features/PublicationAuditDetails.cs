using System.Buffers;
using System.Text;
using System.Text.Json;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features;

/// <summary>
/// The audit details document every publication and rollback of this module
/// writes. One producer for the five paths, so the next change of shape
/// reaches all of them instead of one.
/// <para>
/// The report that arrives here has already passed, and a passed report still
/// carries warnings: a warning never blocks a publication. What the report
/// says about a warning is a sentence built out of the content being
/// published, naming a declared variable or a link host read off a wrapper
/// body, together with the content unit the finding came from. The trail is
/// append-only and hash-chained, so a name that lands here lands forever. This
/// producer therefore keeps the verdict, the catalog that produced it and the
/// warning trace, and drops the text around them.
/// </para>
/// <para>
/// <c>checks</c> earns its space because it is the only field a later
/// revalidation cannot reproduce. The catalog is code: it changes on deploy
/// and carries no stamp, and recent work added checks to it. Without the list,
/// the row does not say which set of rules the version was approved against.
/// </para>
/// <para>
/// <c>warned</c> and <c>warnings</c> earn theirs because publishing over a
/// warning is a decision, and a decision belongs in the trail. The pair keeps
/// that the publisher was warned, by which rule, and how many times, without
/// the message that carries the name derived from the content.
/// </para>
/// <para>
/// There is no <c>failed</c> list and no failed count. On these five paths a
/// failed check is unreachable by construction: a report that does not pass
/// returns a blocked outcome before any audit entry is built, so the field
/// could only ever hold an empty value. A field with one possible value costs
/// bytes forever and answers nothing.
/// </para>
/// <para>
/// Publication and rollback write the same validation object. The asymmetry
/// closed upwards on purpose: <c>{ passed: true }</c> alone, which is what the
/// rollback used to write, does not say which catalog ran nor that anybody was
/// warned, so a rollback recorded that way is a weaker record of the same act.
/// </para>
/// </summary>
internal static class PublicationAuditDetails
{
    internal static string ForPublication(string contentHash, int? supersededVersion, ValidationReport report)
        => Compose(contentHash, supersededVersion, report, schemaVersion: null, rollback: null);

    internal static string ForClassPolicyPublication(
        string contentHash,
        int schemaVersion,
        int? supersededVersion,
        ValidationReport report)
        => Compose(contentHash, supersededVersion, report, schemaVersion, rollback: null);

    internal static string ForRollback(
        string contentHash,
        int publishedVersion,
        int rolledBackFrom,
        int? supersededVersion,
        ValidationReport report)
        => Compose(
            contentHash,
            supersededVersion,
            report,
            schemaVersion: null,
            new RollbackProvenance(publishedVersion, rolledBackFrom));

    private static string Compose(
        string contentHash,
        int? supersededVersion,
        ValidationReport report,
        int? schemaVersion,
        RollbackProvenance? rollback)
    {
        ArgumentNullException.ThrowIfNull(report);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("contentHash", contentHash);
            if (rollback is not null)
            {
                writer.WriteNumber("rolledBackFrom", rollback.RolledBackFrom);
                writer.WriteNumber("publishedVersion", rollback.PublishedVersion);
            }

            if (supersededVersion is int superseded)
            {
                writer.WriteNumber("supersededVersion", superseded);
            }
            else
            {
                writer.WriteNull("supersededVersion");
            }

            if (schemaVersion is int schema)
            {
                writer.WriteNumber("schemaVersion", schema);
            }

            WriteValidation(writer, report);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteValidation(Utf8JsonWriter writer, ValidationReport report)
    {
        writer.WritePropertyName("validation");
        writer.WriteStartObject();
        writer.WriteBoolean("passed", report.Passed);
        WriteNames(writer, "checks", report.Checks);
        WriteNames(
            writer,
            "warned",
            report.Checks.Where(check => check.Status == ValidationCheckStatuses.Warning));
        writer.WriteNumber(
            "warnings",
            report.Checks.Count(check => check.Status == ValidationCheckStatuses.Warning));
        writer.WriteEndObject();
    }

    /// <summary>
    /// Distinct names in ordinal order. A catalog runs a check once per finding
    /// and the finding count is what <c>warnings</c> is for, so repeating the
    /// name would only make the row grow with the content.
    /// </summary>
    private static void WriteNames(Utf8JsonWriter writer, string property, IEnumerable<ValidationCheck> checks)
    {
        writer.WritePropertyName(property);
        writer.WriteStartArray();
        foreach (var name in checks
            .Select(check => check.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal))
        {
            writer.WriteStringValue(name);
        }

        writer.WriteEndArray();
    }

    private sealed record RollbackProvenance(int PublishedVersion, int RolledBackFrom);
}
