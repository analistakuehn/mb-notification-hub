using System.Diagnostics;
using System.Reflection;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// Pins where a deep template source is stopped. The engine parses a postfix
/// chain in a loop rather than a recursion, so it takes 'a.b.b.b...' as far as
/// the source affords and returns a syntax tree of the same depth, and a walk
/// that recursed over such a tree ran out of call stack well inside the
/// character ceiling. What stops that source today is the ceiling on the tokens
/// of a single code block, measured before the parse: it admits 255 links of
/// 'a.b' and refuses everything past that, where the depth that exhausts a one
/// megabyte stack is some nine thousand links.
/// </summary>
/// <remarks>
/// Both ends of that statement run in a process of their own. A stack overflow
/// is not catchable in .NET: it ends the process it happens in, so a regression
/// that let a deep source through would end the test host mid-run, with no
/// failing assertion and no report. The probe reports through its exit code
/// instead, and a regression shows up as a failed test rather than as a run that
/// disappears.
/// </remarks>
public sealed class TemplateSourceNestingContainmentTests
{
    private const string ProbeName = "NotificationHub.StackProbe";
    private const string ProbeDirectoryKey = "StackProbeDirectory";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(1);

    [Theory]
    [InlineData("member")]
    [InlineData("index")]
    public async Task The_deepest_source_the_size_ceiling_accepts_is_refused_before_a_tree_exists(
        string shape)
    {
        ProbeRun run = await RunProbeAsync(shape, "refusal");

        run.ExitCode.ShouldBe(0, run.Report);
    }

    [Theory]
    [InlineData("member")]
    [InlineData("index")]
    public async Task The_deepest_chain_the_complexity_ceiling_admits_is_analyzed_without_ending_the_process(
        string shape)
    {
        ProbeRun run = await RunProbeAsync(shape, "walk");

        run.ExitCode.ShouldBe(0, run.Report);
    }

    private static async Task<ProbeRun> RunProbeAsync(string shape, string check)
    {
        var path = ProbePath();
        File.Exists(path).ShouldBeTrue($"The probe executable is missing at '{path}'.");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(path)
            {
                ArgumentList = { shape, check },
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
