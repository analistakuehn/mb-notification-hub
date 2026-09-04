using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Retention;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Reconciliation;

namespace NotificationHub.UnitTests.AttachmentManagement;

/// <summary>
/// The retention section, read through the binder and the startup guard the
/// host uses. Like the capacity section beside it, nothing here has a default
/// that closes: a window nobody declared cannot quietly become a window of
/// zero, because zero removes the content of every attachment the moment it
/// reaches the state.
/// </summary>
public sealed class AttachmentRetentionOptionsTests
{
    /// <summary>
    /// The file the host ships, read off the source tree and passed through the
    /// same binder and guard. Restating the windows in memory would assert what
    /// the test itself configured; this reads what is on disk, so the approved
    /// retention silently drifting is the one edit this catches.
    /// </summary>
    [Fact]
    public void The_configuration_the_host_ships_declares_every_window()
    {
        AttachmentRetentionOptions options = Shipped();

        options.Enabled.ShouldBeTrue();
        options.UnstartedUpload.ShouldBe(TimeSpan.FromDays(7));
        options.UnvalidatedContent.ShouldBe(TimeSpan.FromDays(7));
        options.RefusedContent.ShouldBe(TimeSpan.FromDays(3));
        options.WithdrawnRelease.ShouldBe(TimeSpan.FromDays(7));
        options.Interval.ShouldBe(TimeSpan.FromHours(1));
        options.BatchSize.ShouldBe(100);
    }

    /// <summary>
    /// The one number here that is derived, held against the job it is derived
    /// from. An attempt nobody reported keeps a dependency over the attachment
    /// until the delivery side resolves it, and that side waits out its own
    /// staleness cut and then runs once per round; the sum is the longest an
    /// attempt can sit unresolved.
    /// <para>
    /// Both halves are read from the file the host ships, not from the
    /// defaults in code, because what a deployment runs with is what decides
    /// the horizon. This is the whole guard against a floor that was derived
    /// once and then stopped being true, and it is the reason the floor may
    /// live in this module as a number instead of as a reading of another
    /// context's settings.
    /// </para>
    /// </summary>
    [Fact]
    public void The_floor_covers_the_horizon_the_delivery_side_resolves_an_unknown_attempt_in()
    {
        DeliveryReconciliationOptions delivery = ShippedConfiguration()
            .GetSection(DeliveryReconciliationOptions.SectionName)
            .Get<DeliveryReconciliationOptions>()
            .ShouldNotBeNull();

        delivery.Interval.ShouldBe(TimeSpan.FromDays(1));
        delivery.StaleAfter.ShouldBe(TimeSpan.FromHours(6));
        AttachmentRetentionOptions.UnresolvedAttemptHorizon
            .ShouldBeGreaterThanOrEqualTo(delivery.Interval + delivery.StaleAfter);

        // And every window the host ships clears that floor, so the guard
        // below is not describing a configuration nobody runs.
        AttachmentRetentionOptions shipped = Shipped();
        Windows(shipped).ShouldAllBe(window =>
            window >= AttachmentRetentionOptions.UnresolvedAttemptHorizon);
    }

    /// <summary>
    /// No section, no process. The failure names all four windows, so this
    /// stays a reading about the section as a whole and not about whichever
    /// guard happens to run first.
    /// </summary>
    [Fact]
    public void Startup_refuses_a_retention_section_that_is_not_there()
    {
        OptionsValidationException failure = Should.Throw<OptionsValidationException>(
            () => Validate([]));

        failure.Failures.ShouldContain(message => message.Contains(
            "Todos os prazos de retenção têm de ser declarados",
            StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("UnstartedUpload")]
    [InlineData("UnvalidatedContent")]
    [InlineData("RefusedContent")]
    [InlineData("WithdrawnRelease")]
    public void Startup_refuses_a_window_nobody_declared(string window)
    {
        OptionsValidationException failure = Should.Throw<OptionsValidationException>(
            () => Validate(Declared(without: window)));

        failure.Failures.ShouldContain(message => message.Contains(
            "Todos os prazos de retenção têm de ser declarados",
            StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("UnstartedUpload")]
    [InlineData("UnvalidatedContent")]
    [InlineData("RefusedContent")]
    [InlineData("WithdrawnRelease")]
    public void Startup_refuses_a_window_below_the_horizon_of_an_unresolved_attempt(string window)
    {
        Dictionary<string, string?> values = Declared();
        values[$"{AttachmentRetentionOptions.SectionName}:{window}"] = "1.05:59:59";

        OptionsValidationException failure = Should.Throw<OptionsValidationException>(
            () => Validate(values));

        failure.Failures.ShouldContain(message => message.Contains(
            "menor que o horizonte de resolução",
            StringComparison.Ordinal));
    }

    /// <summary>
    /// Exactly the floor passes, otherwise the guard above would be refusing
    /// the shortest retention the derivation allows rather than the ones it
    /// exists to catch.
    /// </summary>
    [Fact]
    public void Startup_accepts_a_window_exactly_at_the_horizon()
    {
        AttachmentRetentionOptions options = Resolve(Declared(window: "1.06:00:00"));

        Windows(options).ShouldAllBe(window =>
            window == AttachmentRetentionOptions.UnresolvedAttemptHorizon);
    }

    [Fact]
    public void A_declared_retention_arrives_as_written()
    {
        Dictionary<string, string?> values = Declared();
        values[$"{AttachmentRetentionOptions.SectionName}:RefusedContent"] = "5.00:00:00";
        values[$"{AttachmentRetentionOptions.SectionName}:Enabled"] = "false";

        AttachmentRetentionOptions options = Resolve(values);

        options.Enabled.ShouldBeFalse();
        options.RefusedContent.ShouldBe(TimeSpan.FromDays(5));
        options.Windows().RefusedContent.ShouldBe(TimeSpan.FromDays(5));
        options.Windows().UnstartedUpload.ShouldBe(TimeSpan.FromDays(10));
    }

    private static IReadOnlyList<TimeSpan> Windows(AttachmentRetentionOptions options)
        =>
        [
            options.Windows().UnstartedUpload,
            options.Windows().UnvalidatedContent,
            options.Windows().RefusedContent,
            options.Windows().WithdrawnRelease,
        ];

    /// <summary>
    /// A section whose values are all fine, so a test about one of them is not
    /// answered by a failure over another.
    /// </summary>
    private static Dictionary<string, string?> Declared(
        string window = "10.00:00:00",
        string? without = null)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in (string[])
            ["UnstartedUpload", "UnvalidatedContent", "RefusedContent", "WithdrawnRelease"])
        {
            if (string.Equals(name, without, StringComparison.Ordinal))
            {
                continue;
            }

            values[$"{AttachmentRetentionOptions.SectionName}:{name}"] = window;
        }

        return values;
    }

    private static AttachmentRetentionOptions Shipped()
    {
        var services = new ServiceCollection();
        services.AddAttachmentRetention(ShippedConfiguration());
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IStartupValidator>().Validate();
        return provider.GetRequiredService<IOptions<AttachmentRetentionOptions>>().Value;
    }

    private static IConfigurationRoot ShippedConfiguration()
        => new ConfigurationBuilder()
            .AddJsonFile(ShippedSettingsPath(), optional: false)
            .Build();

    /// <summary>
    /// The configuration file of the host, located from the build output. A
    /// path that stopped resolving would leave the readings above answering
    /// about a file nobody shipped, so it throws instead of falling back.
    /// </summary>
    private static string ShippedSettingsPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        var path = Path.Combine(
            directory?.FullName ?? throw new DirectoryNotFoundException(
                "Could not locate the solution root."),
            "src",
            "Platform.Api",
            "appsettings.json");

        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("The host configuration file was not found.", path);
    }

    private static void Validate(Dictionary<string, string?> values)
    {
        using ServiceProvider provider = Provider(values);
        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    private static AttachmentRetentionOptions Resolve(Dictionary<string, string?> values)
    {
        using ServiceProvider provider = Provider(values);
        provider.GetRequiredService<IStartupValidator>().Validate();
        return provider.GetRequiredService<IOptions<AttachmentRetentionOptions>>().Value;
    }

    private static ServiceProvider Provider(Dictionary<string, string?> values)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddAttachmentRetention(configuration);
        return services.BuildServiceProvider();
    }
}
