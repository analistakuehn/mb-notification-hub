using System.Data.Common;

namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Scheduling;

/// <summary>
/// Parameter binding shared by the scheduler's statements. Every value the
/// scans vary reaches the database as a bind value and never as text spliced
/// into a statement; what is written literally is only the predicate the
/// planner has to see to pick a partial index, and none of it comes from
/// outside this assembly.
/// </summary>
internal static class ScanCommands
{
    internal static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
