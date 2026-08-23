using System.Diagnostics;

namespace NotificationHub.IntegrationTests.TemplateManagement;

/// <summary>
/// Probes the local Docker daemon once per test run. Postgres-backed tests skip
/// with an explicit reason when the daemon is unreachable; they never fake green.
/// </summary>
public static class DockerEnvironment
{
    public const string SkipReason =
        "Docker daemon is not available; this Postgres-backed integration test was skipped, not executed.";

    private static readonly Lazy<bool> Available = new(Probe);

    public static bool IsAvailable => Available.Value;

    private static bool Probe()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "info")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            return process is not null && process.WaitForExit(15_000) && process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }
}

/// <summary>A fact that runs only when the Docker daemon responds.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresDockerFactAttribute : FactAttribute
{
    public RequiresDockerFactAttribute()
    {
        if (!DockerEnvironment.IsAvailable)
        {
            Skip = DockerEnvironment.SkipReason;
        }
    }
}

/// <summary>A theory that runs only when the Docker daemon responds.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresDockerTheoryAttribute : TheoryAttribute
{
    public RequiresDockerTheoryAttribute()
    {
        if (!DockerEnvironment.IsAvailable)
        {
            Skip = DockerEnvironment.SkipReason;
        }
    }
}
