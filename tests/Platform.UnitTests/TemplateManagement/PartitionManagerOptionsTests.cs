using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Partitioning;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class PartitionManagerOptionsTests
{
    [Fact]
    public void Defaults_run_daily_two_months_ahead_over_the_audit_table_with_the_phase_gates_off()
    {
        var options = new PartitionManagerOptions();

        options.Enabled.ShouldBeTrue();
        options.Interval.ShouldBe(TimeSpan.FromDays(1));
        options.MonthsAhead.ShouldBe(2);
        options.PartitionedTables.ShouldBe(["audit_event"]);
        options.EnableRevokeOnClosedPartitions.ShouldBeFalse();
        options.EnableRetentionCycle.ShouldBeFalse();
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

        options.PartitionedTables.ShouldBe(["audit_event"]);
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
