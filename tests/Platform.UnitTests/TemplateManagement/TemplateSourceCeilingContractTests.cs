using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// The one number that governs the template source axis, read from both ends:
/// what the host refuses to start with, and what the shipped defaults say.
/// <para>
/// The class holds two kinds of test on purpose and each says which it is. The
/// first kind was written red and describes behavior this change introduced.
/// The second kind was written green: it does not describe new behavior, it
/// pins a premise another decision was taken on, and the day it turns red that
/// decision is what has to be reopened.
/// </para>
/// </summary>
public sealed class TemplateSourceCeilingContractTests
{
    [Fact]
    public void A_configured_ceiling_above_the_source_ceiling_refuses_to_start()
    {
        // The ceiling the aggregates enforce is not configurable, so a
        // configuration that asks for more is asking for a limit nothing
        // downstream will honor. It is refused at the door rather than
        // ignored in silence.
        Should.Throw<OptionsValidationException>(() => ValidateStartup(200_000));
    }

    [Fact]
    public void A_configured_ceiling_below_the_longest_subject_refuses_to_start()
    {
        // A subject is source the engine analyzes, so a ceiling under the
        // longest subject a version may carry recreates on the subject axis
        // the same dead band: the write is accepted and the analysis refuses
        // it, with a message that still calls the field a template.
        Should.Throw<OptionsValidationException>(() => ValidateStartup(500));
    }

    [Fact]
    public void The_shipped_default_matches_the_domain_ceiling()
    {
        // Born green, and kept as a guard. Reading the configured ceiling
        // inside the validators was considered and refused, and this equality
        // is the premise that refusal rests on: while the default is the
        // domain ceiling, the two axes cannot disagree without an operator
        // typing a number. The day this turns red the refusal is reopened,
        // not the assertion.
        //
        // The compile-time assertion beside the parse memoization cannot say
        // this. It expresses an inequality, which a default of any smaller
        // value would also satisfy, so the two do not overlap.
        new TemplatingOptions().MaxTemplateSizeChars.ShouldBe(TemplateSourceSize.MaxChars);
    }

    [Fact]
    public void No_shipped_configuration_file_tightens_the_source_ceiling()
    {
        // Born green, and it is the literal trigger to reopen the decision
        // above. An operator tightening this key is the one move that splits
        // the two axes again, and a file shipped inside the repository is the
        // move made where a review can still see it.
        var swept = ShippedSettingsFiles();
        string[] tightened =
        [
            .. swept
                .Select(path => (path, configured: ConfiguredCeiling(path)))
                .Where(item => item.configured is not null && item.configured < TemplateSourceSize.MaxChars)
                .Select(item => $"{item.path}:{item.configured}"),
        ];

        // A sweep that reached nothing agrees with a clean sweep, so what was
        // reached is asserted before what was found.
        swept.Length.ShouldBeGreaterThanOrEqualTo(2);
        swept.ShouldContain(path => path.Contains("Platform.Api", StringComparison.Ordinal));
        swept.ShouldContain(path => path.Contains("Platform.Worker", StringComparison.Ordinal));
        tightened.ShouldBeEmpty();
    }

    [Fact]
    public void The_subject_ceiling_fits_inside_the_source_ceiling()
    {
        // Born green. The subject ceiling comes from the mail header line
        // length and from the column that stores it, never from the cost of
        // parsing it, so the two numbers move for unrelated reasons. They only
        // stay compatible while this holds.
        TemplateVersion.MaxSubjectLength.ShouldBeLessThanOrEqualTo(TemplateSourceSize.MaxChars);
    }

    private static void ValidateStartup(int maxTemplateSizeChars)
    {
        var services = new ServiceCollection();
        services.AddTemplateManagementTemplating(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{TemplatingOptions.SectionName}:MaxTemplateSizeChars"] =
                    maxTemplateSizeChars.ToString(CultureInfo.InvariantCulture),
            })
            .Build());

        using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    /// <summary>
    /// The settings files both hosts ship with, read from the repository and
    /// never from build output, so a stale copy under bin cannot answer for
    /// what is committed.
    /// </summary>
    private static string[] ShippedSettingsFiles()
    {
        string[] hosts = ["Platform.Api", "Platform.Worker"];
        return
        [
            .. hosts
                .Select(host => Path.Combine(FindSolutionRoot(), "src", host))
                .Where(Directory.Exists)
                .SelectMany(root => Directory.EnumerateFiles(
                    root,
                    "appsettings*.json",
                    SearchOption.TopDirectoryOnly))
                .Order(StringComparer.Ordinal),
        ];
    }

    private static int? ConfiguredCeiling(string settingsFile)
    {
        var value = new ConfigurationBuilder()
            .AddJsonFile(settingsFile, optional: false)
            .Build()
            .GetSection(TemplatingOptions.SectionName)["MaxTemplateSizeChars"];

        return value is null ? null : int.Parse(value, CultureInfo.InvariantCulture);
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the solution root.");
    }
}
