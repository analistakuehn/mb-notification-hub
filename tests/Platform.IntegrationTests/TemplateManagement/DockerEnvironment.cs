using System.Diagnostics;

namespace NotificationHub.IntegrationTests.TemplateManagement;

/// <summary>
/// Probes the local Docker daemon once per test run. Postgres-backed tests skip
/// with an explicit reason when the daemon is unreachable; they never fake green.
/// <para>
/// Skipping is right on a workstation and wrong on the run that grades a
/// release. A skipped test passes, so an environment where the daemon never
/// answered produces the same green suite as one where every scenario ran, and
/// the exit criteria of a phase are proven by exactly these tests. The
/// environment variable is how a run declares which of the two it is: set, an
/// unreachable daemon is a failure rather than a skip, and the run that was
/// supposed to prove the criteria cannot report success without them.
/// </para>
/// <para>
/// Unset is the default because the alternative would make the ordinary
/// workstation run red for a reason nobody asked it to care about. The gate is
/// therefore something a pipeline opts into, and a pipeline that forgets is a
/// pipeline that proves less than it claims, which is the same class of gap
/// this variable exists to close and is why it belongs in the pipeline
/// definition rather than in a comment.
/// </para>
/// </summary>
public static class DockerEnvironment
{
    public const string SkipReason =
        "Docker daemon is not available; this Postgres-backed integration test was skipped, not executed.";

    /// <summary>
    /// Environment variable that turns an unavailable daemon into a failure.
    /// Any non-empty value other than the literals that spell falsehood counts
    /// as set, because a pipeline that exports it at all means to require it.
    /// </summary>
    public const string RequiredVariable = "NOTIFICATIONHUB_REQUIRE_DOCKER";

    public const string MissingDaemonFailure =
        "Docker daemon is not available and " + RequiredVariable + " is set: this run was asked to "
        + "prove the Postgres-backed criteria and cannot report success without executing them.";

    private static readonly Lazy<bool> Available = new(Probe);

    private static readonly Lazy<bool> Required = new(ProbeRequirement);

    public static bool IsAvailable => Available.Value;

    /// <summary>Whether this run refuses to skip on an unreachable daemon.</summary>
    public static bool IsRequired => Required.Value;

    /// <summary>
    /// The skip reason for a run that tolerates a missing daemon, or null for a
    /// run that requires it. Null is what makes the test execute and fail on
    /// the fixture instead of disappearing from the report.
    /// </summary>
    internal static string? SkipReasonWhenUnavailable()
        => IsAvailable || IsRequired ? null : SkipReason;

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

    private static bool ProbeRequirement()
    {
        var value = Environment.GetEnvironmentVariable(RequiredVariable);
        return value is { Length: > 0 }
            && !string.Equals(value, "0", StringComparison.Ordinal)
            && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A fact that runs only when the Docker daemon responds.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresDockerFactAttribute : FactAttribute
{
    public RequiresDockerFactAttribute()
        => Skip = DockerEnvironment.SkipReasonWhenUnavailable();
}

/// <summary>A theory that runs only when the Docker daemon responds.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresDockerTheoryAttribute : TheoryAttribute
{
    public RequiresDockerTheoryAttribute()
        => Skip = DockerEnvironment.SkipReasonWhenUnavailable();
}
