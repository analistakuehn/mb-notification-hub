using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Capability;

namespace NotificationHub.UnitTests.AttachmentManagement;

/// <summary>
/// The deployment state of the attachment capability, read through the binder
/// the host uses.
/// <para>
/// It is the one section of this module whose absence is a legitimate state,
/// and the state it means is the closed one. Its neighbours refuse an unset
/// value at startup because an unset ceiling or an unset retention window would
/// be a product decision taken by omission; here the omission is the decision,
/// and the whole point of these cases is that it stays the safe one however the
/// configuration is shaped.
/// </para>
/// <para>
/// It is also not the emergency control. That one means permitted unless a row
/// says otherwise, so a missing row lets work through; this one lets nothing
/// through until configuration says so, and the two could not share an artifact
/// without giving one absence opposite consequences.
/// </para>
/// </summary>
public sealed class AttachmentCapabilityOptionsTests
{
    /// <summary>
    /// Nothing configured at all. It is the deployment the capability ships as,
    /// and the answer has to be the closed one without any guard having to run.
    /// </summary>
    [Fact]
    public void A_configuration_that_never_names_the_section_takes_no_new_attachments()
    {
        Resolve([]).AcceptsNewAttachments.ShouldBeFalse();
        Gate([]).AcceptsNewAttachments.ShouldBeFalse();
    }

    /// <summary>
    /// The section is there and the value is not. Binding a section that names
    /// something else must not be read as an opinion about this member.
    /// </summary>
    [Fact]
    public void A_section_that_names_no_value_takes_no_new_attachments()
    {
        Resolve(new Dictionary<string, string?>
        {
            [$"{AttachmentCapabilityOptions.SectionName}:SomethingElse"] = "true",
        }).AcceptsNewAttachments.ShouldBeFalse();
    }

    /// <summary>
    /// The type itself, with no binder involved. The closed answer is the
    /// language default of the member and not a line anyone wrote, which is
    /// what keeps a single edit from opening the capability.
    /// </summary>
    [Fact]
    public void A_fresh_options_object_takes_no_new_attachments()
        => new AttachmentCapabilityOptions().AcceptsNewAttachments.ShouldBeFalse();

    /// <summary>
    /// The other direction, so the closed answers above are a refusal and not a
    /// binder that never read anything.
    /// </summary>
    [Fact]
    public void The_section_that_says_so_takes_new_attachments()
    {
        Dictionary<string, string?> enabled = new()
        {
            [$"{AttachmentCapabilityOptions.SectionName}:AcceptsNewAttachments"] = "true",
        };

        Resolve(enabled).AcceptsNewAttachments.ShouldBeTrue();
        Gate(enabled).AcceptsNewAttachments.ShouldBeTrue();
    }

    /// <summary>
    /// The file the host ships, read off the source tree instead of restated
    /// here. A test that configured the value itself would assert its own
    /// arrangement; this one fails the day the shipped deployment stops being
    /// the closed one.
    /// </summary>
    [Fact]
    public void The_configuration_the_host_ships_takes_no_new_attachments()
    {
        IConfigurationRoot shipped = new ConfigurationBuilder()
            .AddJsonFile(ShippedSettingsPath(), optional: false)
            .Build();
        var services = new ServiceCollection();
        services.AddAttachmentCapability(shipped);
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<AttachmentCapabilityOptions>>()
            .Value.AcceptsNewAttachments.ShouldBeFalse();
    }

    private static AttachmentCapabilityOptions Resolve(Dictionary<string, string?> values)
    {
        using ServiceProvider provider = Provider(values);
        return provider.GetRequiredService<IOptions<AttachmentCapabilityOptions>>().Value;
    }

    private static AttachmentCapability Gate(Dictionary<string, string?> values)
    {
        using ServiceProvider provider = Provider(values);
        return provider.GetRequiredService<AttachmentCapability>();
    }

    private static ServiceProvider Provider(Dictionary<string, string?> values)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddAttachmentCapability(configuration);
        return services.BuildServiceProvider();
    }

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
}
