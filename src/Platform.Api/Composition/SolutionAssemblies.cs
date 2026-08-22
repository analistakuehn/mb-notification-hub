using System.Reflection;

namespace NotificationHub.Api.Composition;

/// <summary>Production assemblies scanned for module and endpoint registrations.</summary>
public static class SolutionAssemblies
{
    public static Assembly[] All { get; } =
    [
        typeof(NotificationHub.Api.AssemblyMarker).Assembly,
    ];
}
