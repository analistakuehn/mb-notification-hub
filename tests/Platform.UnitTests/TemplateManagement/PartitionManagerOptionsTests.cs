using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Partitioning;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class PartitionManagerOptionsTests
{
    [Fact]
    public void Defaults_run_daily_two_months_ahead_with_the_phase_gates_off()
    {
        var options = new PartitionManagerOptions();

        options.Enabled.ShouldBeTrue();
        options.Interval.ShouldBe(TimeSpan.FromDays(1));
        options.MonthsAhead.ShouldBe(2);

        // Empty on purpose: configuration binding appends to a non-empty
        // default, so the table default lives on the consumer side.
        options.PartitionedTables.ShouldBeEmpty();
        options.FutureWindowMinimumDays.ShouldBe(21);
        options.EnableRevokeOnClosedPartitions.ShouldBeFalse();
        options.EnableRetentionCycle.ShouldBeFalse();
    }

    [Fact]
    public void The_consumer_falls_back_to_the_audit_table_when_no_table_is_configured()
    {
        PartitionMaintenance.EffectiveTables(new PartitionManagerOptions())
            .ShouldBe(["audit_event"]);
    }

    [Fact]
    public void A_configured_table_list_replaces_the_default_instead_of_appending_to_it()
    {
        PartitionManagerOptions options = ResolveOptions(new Dictionary<string, string?>
        {
            ["Modules:TemplateManagement:PartitionManager:PartitionedTables:0"] = "outbox_event",
        });

        PartitionMaintenance.EffectiveTables(options).ShouldBe(["outbox_event"]);
    }

    [Fact]
    public void Rejects_a_table_name_that_is_not_a_plain_lowercase_identifier()
    {
        OptionsValidationException exception = Should.Throw<OptionsValidationException>(
            () => ResolveOptions(new Dictionary<string, string?>
            {
                ["Modules:TemplateManagement:PartitionManager:PartitionedTables:0"] =
                    "audit_event; DROP TABLE templates",
            }));

        exception.Failures.ShouldContain(failure =>
            failure.Contains("identificadores", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_an_interval_shorter_than_one_minute()
    {
        Should.Throw<OptionsValidationException>(
            () => ResolveOptions(new Dictionary<string, string?>
            {
                ["Modules:TemplateManagement:PartitionManager:Interval"] = "00:00:05",
            }));
    }

    [Fact]
    public void Rejects_an_interval_longer_than_thirty_days()
    {
        OptionsValidationException exception = Should.Throw<OptionsValidationException>(
            () => ResolveOptions(new Dictionary<string, string?>
            {
                ["Modules:TemplateManagement:PartitionManager:Interval"] = "31.00:00:00",
            }));

        exception.Failures.ShouldContain(failure =>
            failure.Contains("trinta dias", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_a_month_window_outside_one_to_twelve()
    {
        Should.Throw<OptionsValidationException>(
            () => ResolveOptions(new Dictionary<string, string?>
            {
                ["Modules:TemplateManagement:PartitionManager:MonthsAhead"] = "0",
            }));
    }

    [Fact]
    public void Accepts_an_empty_configuration_section_by_falling_back_to_the_defaults()
    {
        PartitionManagerOptions options = ResolveOptions([]);

        options.PartitionedTables.ShouldBeEmpty();
        PartitionMaintenance.EffectiveTables(options).ShouldBe(["audit_event"]);
        options.Interval.ShouldBe(TimeSpan.FromDays(1));
    }

    private static PartitionManagerOptions ResolveOptions(Dictionary<string, string?> values)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var services = new ServiceCollection();
        services.AddTemplateManagementPartitionManager(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<PartitionManagerOptions>>().Value;
    }
}
