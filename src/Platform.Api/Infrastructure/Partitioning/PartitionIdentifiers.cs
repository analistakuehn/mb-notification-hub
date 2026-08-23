using System.Text.RegularExpressions;

namespace NotificationHub.Api.Infrastructure.Partitioning;

/// <summary>
/// Identifier rule shared by every partition-provisioning consumer: an
/// unquoted lowercase PostgreSQL identifier. Anything else is rejected before
/// reaching the DDL.
/// </summary>
internal static partial class PartitionIdentifiers
{
    internal static bool IsSafeIdentifier(string? value)
        => value is not null && SafeIdentifier().IsMatch(value);

    [GeneratedRegex("^[a-z][a-z0-9_]{0,47}$")]
    private static partial Regex SafeIdentifier();
}
