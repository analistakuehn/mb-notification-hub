using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Capacity;

namespace NotificationHub.UnitTests.AttachmentManagement;

/// <summary>
/// The capacity section, read through the binder and the startup guard the host
/// uses. The section next to it defaults to a shape that releases nothing, and
/// this one deliberately does not: a capacity nobody declared cannot quietly
/// become a capacity of zero, because zero refuses every notification carrying
/// an attachment rather than refusing an attachment.
/// </summary>
public sealed class AttachmentCapacityOptionsTests
{
    /// <summary>
    /// The file the host ships, read off the source tree and passed through the
    /// same binder and guard. Restating the numbers in memory would assert what
    /// the test itself configured; this reads what is on disk, so the approved
    /// capacity silently drifting is the one edit this catches.
    /// </summary>
    [Fact]
    public void The_configuration_the_host_ships_declares_the_approved_capacity()
    {
        IConfigurationRoot shipped = new ConfigurationBuilder()
            .AddJsonFile(ShippedSettingsPath(), optional: false)
            .Build();
        var services = new ServiceCollection();
        services.AddAttachmentCapacity(shipped);
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IStartupValidator>().Validate();
        AttachmentCapacityOptions options = provider
            .GetRequiredService<IOptions<AttachmentCapacityOptions>>()
            .Value;

        options.MaxAttachmentBytes.ShouldBe(7_340_032);
        options.MaxEnvelopeBytes.ShouldBe(7_340_032);
        options.MaxAttachmentsPerNotification.ShouldBe(10);

        // The ceiling of one attachment is the ceiling of the whole set. What
        // limits the cost of a send is the sum, so a smaller per attachment
        // ceiling would forbid one large attachment while allowing the same
        // bytes split across several.
        options.MaxAttachmentBytes.ShouldBe(options.MaxEnvelopeBytes);
    }

    /// <summary>
    /// No section, no process. Every value is named in the failure, so this
    /// stays a reading about the section as a whole and not about whichever
    /// guard happens to run first.
    /// </summary>
    [Fact]
    public void Startup_refuses_a_capacity_section_that_is_not_there()
    {
        OptionsValidationException failure = Should.Throw<OptionsValidationException>(
            () => Validate([]));

        failure.Failures.ShouldContain(message =>
            message.Contains("O teto por anexo tem de ser declarado", StringComparison.Ordinal));
        failure.Failures.ShouldContain(message => message.Contains(
            "O envelope somado por notificação tem de ser declarado",
            StringComparison.Ordinal));
        failure.Failures.ShouldContain(message => message.Contains(
            "A quantidade máxima de anexos por notificação tem de ser declarada",
            StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Startup_refuses_a_per_attachment_ceiling_that_admits_nothing(string ceiling)
    {
        OptionsValidationException failure = Should.Throw<OptionsValidationException>(
            () => Validate(Declared(maxAttachmentBytes: ceiling)));

        failure.Failures.ShouldContain(message =>
            message.Contains("O teto por anexo tem de ser declarado", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Startup_refuses_an_envelope_that_admits_nothing(string envelope)
    {
        OptionsValidationException failure = Should.Throw<OptionsValidationException>(
            () => Validate(Declared(maxEnvelopeBytes: envelope)));

        failure.Failures.ShouldContain(message => message.Contains(
            "O envelope somado por notificação tem de ser declarado",
            StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Startup_refuses_a_quantity_that_admits_nothing(string quantity)
    {
        OptionsValidationException failure = Should.Throw<OptionsValidationException>(
            () => Validate(Declared(maxAttachmentsPerNotification: quantity)));

        failure.Failures.ShouldContain(message => message.Contains(
            "A quantidade máxima de anexos por notificação tem de ser declarada",
            StringComparison.Ordinal));
    }

    /// <summary>
    /// The defect this section exists to close, as a guard. A per attachment
    /// ceiling above the summed envelope admits, at registration, an attachment
    /// no notification could ever carry, and the producer finds out only after
    /// spending the transfer.
    /// </summary>
    [Fact]
    public void Startup_refuses_a_per_attachment_ceiling_above_the_summed_envelope()
    {
        OptionsValidationException failure = Should.Throw<OptionsValidationException>(
            () => Validate(Declared(
                maxAttachmentBytes: "2049",
                maxEnvelopeBytes: "2048")));

        failure.Failures.ShouldContain(message => message.Contains(
            "não pode ultrapassar o envelope somado",
            StringComparison.Ordinal));
    }

    /// <summary>
    /// Equal is the shipped shape and it has to pass, otherwise the guard above
    /// would be refusing the arrangement the module runs on rather than the one
    /// it exists to catch.
    /// </summary>
    [Fact]
    public void Startup_accepts_a_per_attachment_ceiling_equal_to_the_summed_envelope()
        => Validate(Declared(maxAttachmentBytes: "2048", maxEnvelopeBytes: "2048"));

    [Fact]
    public void A_declared_capacity_arrives_as_written()
    {
        AttachmentCapacityOptions options = Resolve(Declared(
            maxAttachmentBytes: "1024",
            maxEnvelopeBytes: "4096",
            maxAttachmentsPerNotification: "3"));

        options.MaxAttachmentBytes.ShouldBe(1024);
        options.MaxEnvelopeBytes.ShouldBe(4096);
        options.MaxAttachmentsPerNotification.ShouldBe(3);
    }

    /// <summary>
    /// A section whose values are all fine, so a test about one of them is not
    /// answered by a failure over another.
    /// </summary>
    private static Dictionary<string, string?> Declared(
        string maxAttachmentBytes = "2048",
        string maxEnvelopeBytes = "4096",
        string maxAttachmentsPerNotification = "3")
        => new()
        {
            [$"{AttachmentCapacityOptions.SectionName}:MaxAttachmentBytes"] =
                maxAttachmentBytes,
            [$"{AttachmentCapacityOptions.SectionName}:MaxEnvelopeBytes"] = maxEnvelopeBytes,
            [$"{AttachmentCapacityOptions.SectionName}:MaxAttachmentsPerNotification"] =
                maxAttachmentsPerNotification,
        };

    /// <summary>
    /// The configuration file of the host, located from the build output. A path
    /// that stopped resolving would leave the reading above answering about a
    /// file nobody shipped, so it throws instead of falling back.
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

    private static AttachmentCapacityOptions Resolve(Dictionary<string, string?> values)
    {
        using ServiceProvider provider = Provider(values);
        return provider.GetRequiredService<IOptions<AttachmentCapacityOptions>>().Value;
    }

    private static ServiceProvider Provider(Dictionary<string, string?> values)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddAttachmentCapacity(configuration);
        return services.BuildServiceProvider();
    }
}
