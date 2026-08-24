using System.Globalization;

namespace NotificationHub.Api.Infrastructure.Messaging.Relay;

/// <summary>
/// Priority bands the relay drains in fixed order. The band is a reader-side
/// classification: the stored <c>priority_class</c> contract does not change,
/// the relay only decides the drain order from it and from the destination.
/// </summary>
internal enum OutboxBand
{
    /// <summary>Authentication traffic; always drained first.</summary>
    Auth = 0,

    /// <summary>Critical class outside the authentication destination.</summary>
    Critical = 1,

    Transactional = 2,

    /// <summary>Operational class and any unknown priority class.</summary>
    Operational = 3,
}

/// <summary>
/// Band vocabulary and classification of the relay reader. The band is stored
/// beside the row as a column the database computes from the same two values
/// <see cref="Classify"/> reads, so the claim compares a column instead of
/// evaluating an expression no index can answer. The two definitions are the
/// same rule written twice, once in C# and once in SQL; an integration test
/// confronts them over every known destination, and they change together.
/// </summary>
internal static class OutboxBands
{
    /// <summary>
    /// The core destination classified into the auth band regardless of the
    /// stored priority class. Dispatch destinations join the band through the
    /// auth suffix over the dispatch prefix.
    /// </summary>
    internal const string AuthDestination = "core-auth";

    /// <summary>Prefix of every dispatch queue destination.</summary>
    internal const string DispatchDestinationPrefix = "dispatch-";

    /// <summary>Suffix that routes a dispatch destination into the auth band.</summary>
    internal const string AuthDestinationSuffix = "-auth";

    private const string CriticalClass = "critical";
    private const string TransactionalClass = "transactional";

    /// <summary>
    /// The stored form of <see cref="Classify"/>: the expression that computes
    /// the band of a row from the destination and the priority class the
    /// producer already wrote. It is the definition of a generated column, so
    /// it evaluates once per insert and never at read time, and no writer can
    /// leave it unset or set it to something else.
    /// </summary>
    internal static readonly string ClassificationSql = string.Create(
        CultureInfo.InvariantCulture,
        $"CASE WHEN destination = '{AuthDestination}' "
        + $"OR (destination LIKE '{DispatchDestinationPrefix}%' "
        + $"AND destination LIKE '%{AuthDestinationSuffix}') THEN {(int)OutboxBand.Auth} "
        + $"WHEN priority_class = '{CriticalClass}' THEN {(int)OutboxBand.Critical} "
        + $"WHEN priority_class = '{TransactionalClass}' THEN {(int)OutboxBand.Transactional} "
        + $"ELSE {(int)OutboxBand.Operational} END");

    /// <summary>Every band, in the order a full instance drains them.</summary>
    internal static readonly OutboxBand[] DrainOrder =
    [
        OutboxBand.Auth,
        OutboxBand.Critical,
        OutboxBand.Transactional,
        OutboxBand.Operational,
    ];

    internal static OutboxBand Classify(string destination, string priorityClass)
    {
        if (string.Equals(destination, AuthDestination, StringComparison.Ordinal)
            || destination.StartsWith(DispatchDestinationPrefix, StringComparison.Ordinal)
            && destination.EndsWith(AuthDestinationSuffix, StringComparison.Ordinal))
        {
            return OutboxBand.Auth;
        }

        return priorityClass switch
        {
            CriticalClass => OutboxBand.Critical,
            TransactionalClass => OutboxBand.Transactional,
            // Unknown classes drain with the lowest band instead of starving.
            _ => OutboxBand.Operational,
        };
    }

    internal static bool TryParseName(string name, out OutboxBand band)
    {
        switch (name)
        {
            case "auth":
                band = OutboxBand.Auth;
                return true;
            case CriticalClass:
                band = OutboxBand.Critical;
                return true;
            case TransactionalClass:
                band = OutboxBand.Transactional;
                return true;
            case "operational":
                band = OutboxBand.Operational;
                return true;
            default:
                band = default;
                return false;
        }
    }

    /// <summary>
    /// The drain order restricted to the configured band names; the configured
    /// order never reorders the drain, only selects from it. An empty
    /// configuration selects every band.
    /// </summary>
    internal static OutboxBand[] Restrict(IReadOnlyCollection<string> bandNames)
    {
        if (bandNames.Count == 0)
        {
            return DrainOrder;
        }

        var selected = new HashSet<OutboxBand>();
        foreach (var name in bandNames)
        {
            if (TryParseName(name, out OutboxBand band))
            {
                selected.Add(band);
            }
        }

        return [.. DrainOrder.Where(selected.Contains)];
    }
}
