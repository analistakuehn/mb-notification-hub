using System.Globalization;

namespace NotificationHub.Api.Modules.Compliance.Features.Reporting;

/// <summary>
/// Object keys of the recurring evidence this module composes. A key is a pure
/// function of what the report covers, so a rerun addresses exactly the object
/// the first run wrote and an auditor navigates the archive without an index.
/// </summary>
/// <remarks>
/// The key is relative: the archiving module decides where evidence lives.
/// The format version travels in the name because the destination is
/// immutable, so a later shape must land beside the earlier one instead of
/// colliding with an object nobody can replace.
/// </remarks>
internal static class EvidenceReportKeys
{
    internal const string MonthlyFolder = "monthly";

    /// <summary>Key of the monthly report of one calendar month, in UTC.</summary>
    internal static string Monthly(int year, int month, int formatVersion)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{MonthlyFolder}/{year:D4}-{month:D2}/evidence-report.v{formatVersion}.json");

    /// <summary>The month a report covers, spelled the way its folder spells it.</summary>
    internal static string MonthName(int year, int month)
        => string.Create(CultureInfo.InvariantCulture, $"{year:D4}-{month:D2}");
}
