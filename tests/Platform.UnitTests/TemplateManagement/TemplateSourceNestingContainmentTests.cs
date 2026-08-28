using System.Diagnostics;
using System.Reflection;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// Proves the analysis survives the deepest source the templating ceiling
/// accepts. The engine parses a postfix chain in a loop rather than a
/// recursion, so it takes 'a.b.b.b...' as far as the character ceiling allows
/// and returns a syntax tree of the same depth; a walk that recursed over that
/// tree ran out of call stack at roughly a fifth of the ceiling.
/// </summary>
/// <remarks>
/// A stack overflow is not catchable in .NET: it ends the process it happens
/// in. Running the deep source here would end the test host mid-run, with no
/// failing assertion and no report, so the analysis runs in a process of its
/// own and this test reads its exit code. A regression therefore shows up as a
/// failed test and not as a run that disappears.
/// </remarks>
public sealed class TemplateSourceNestingContainmentTests
{
    private const string ProbeName = "NotificationHub.StackProbe";
    private const string ProbeDirectoryKey = "StackProbeDirectory";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(1);

    [Theory]
    [InlineData("member")]
    [InlineData("index")]
    public async Task The_deepest_source_the_ceiling_accepts_is_analyzed_without_ending_the_process(
        string shape)
    {
        ProbeRun run = await RunProbeAsync(shape);

        run.ExitCode.ShouldBe(0, run.Report);
    }

    private static async Task<ProbeRun> RunProbeAsync(string shape)
    {
        var path = ProbePath();
        File.Exists(path).ShouldBeTrue($"The probe executable is missing at '{path}'.");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(path, shape)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();

        using var deadline = new CancellationTokenSource(ProbeTimeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return new ProbeRun(-1, $"The probe did not finish within {ProbeTimeout}.");
        }

        return new ProbeRun(process.ExitCode, await output + await error);
    }

    private static string ProbePath()
    {
        AssemblyMetadataAttribute? directory = typeof(TemplateSourceNestingContainmentTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == ProbeDirectoryKey);
        directory.ShouldNotBeNull($"The build did not record '{ProbeDirectoryKey}'.");

        var executable = OperatingSystem.IsWindows() ? $"{ProbeName}.exe" : ProbeName;
        return Path.GetFullPath(Path.Combine(directory.Value!, executable));
    }

    /// <summary>What the probe process left behind, exit code and all it wrote.</summary>
    private sealed record ProbeRun(int ExitCode, string Report);
}
