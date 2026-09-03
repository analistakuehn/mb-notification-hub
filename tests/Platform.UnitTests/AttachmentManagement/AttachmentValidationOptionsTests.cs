using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Validation;

namespace NotificationHub.UnitTests.AttachmentManagement;

public sealed class AttachmentValidationOptionsTests
{
    [Fact]
    public void The_defaults_admit_nothing_and_nothing_admitted_releases_nothing()
    {
        AttachmentValidationOptions options = Resolve([]);

        // Empty on purpose, twice over: it is the decided value, and it is also
        // the shape the binder needs, because configuration appends to a
        // non-empty default instead of replacing it.
        options.AdmittedContentTypes.ShouldBeEmpty();
        options.ReleaseValidity.ShouldBe(TimeSpan.FromDays(30));
        options.InconclusiveWindow.ShouldBe(TimeSpan.FromHours(24));

        // Unset is the strict reading: no grace, and the deadline counted from
        // the release alone.
        options.ValidityEffectiveFrom.ShouldBeNull();
    }

    /// <summary>
    /// The file the host ships, read from the source tree and passed through
    /// the same binder and the same startup guard the host uses. Restating the
    /// values in memory would assert what the test itself configured; this
    /// reads what is on disk, so an entry added to the admitted list of the
    /// base configuration, which would open the gate for every environment at
    /// once, is the one edit this catches.
    /// </summary>
    [Fact]
    public void The_configuration_the_host_ships_admits_nothing_and_passes_the_startup_guard()
    {
        IConfigurationRoot shipped = new ConfigurationBuilder()
            .AddJsonFile(ShippedSettingsPath(), optional: false)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAttachmentValidation(shipped);
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IStartupValidator>().Validate();
        AttachmentValidationOptions options = provider
            .GetRequiredService<IOptions<AttachmentValidationOptions>>()
            .Value;

        options.AdmittedContentTypes.ShouldBeEmpty();
        options.ReleaseValidity.ShouldBe(TimeSpan.FromDays(30));
        options.InconclusiveWindow.ShouldBe(TimeSpan.FromDays(1));
        options.ValidityEffectiveFrom.ShouldBeNull();
    }

    [Fact]
    public void A_configured_list_of_admitted_types_arrives_as_written()
    {
        AttachmentValidationOptions options = Resolve(new Dictionary<string, string?>
        {
            ["Modules:AttachmentManagement:Validation:AdmittedContentTypes:0"] =
                "application/pdf",
        });

        options.AdmittedContentTypes.ShouldBe(["application/pdf"]);
    }

    [Fact]
    public void A_configured_deployment_instant_arrives_as_written()
    {
        AttachmentValidationOptions options = Resolve(new Dictionary<string, string?>
        {
            ["Modules:AttachmentManagement:Validation:ValidityEffectiveFrom"] =
                "2026-09-02T12:00:00Z",
        });

        options.ValidityEffectiveFrom.ShouldBe(
            new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// The guard that keeps the two halves of the type rule from drifting
    /// apart. A type nothing detects would refuse every file of that type as
    /// unrecognized, and that reads in production as the feature being broken
    /// rather than as configuration being wrong.
    /// </summary>
    [Theory]
    [InlineData("application/zip")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("text/csv")]
    [InlineData("not a media type")]
    public void Startup_refuses_a_type_the_signature_table_cannot_detect(string admitted)
    {
        OptionsValidationException failure = Should.Throw<OptionsValidationException>(
            () => Validate(new Dictionary<string, string?>
            {
                ["Modules:AttachmentManagement:Validation:AdmittedContentTypes:0"] = admitted,
            }));

        failure.Failures.ShouldContain(message =>
            message.Contains("assinaturas", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("IMAGE/PNG")]
    [InlineData("application/pdf")]
    public void Startup_accepts_a_type_the_signature_table_detects(string admitted)
        => Validate(new Dictionary<string, string?>
        {
            ["Modules:AttachmentManagement:Validation:AdmittedContentTypes:0"] = admitted,
        });

    /// <summary>
    /// A validity of zero or less is refused at startup because the release row
    /// refuses it at the insert: the table requires the expiry to be after the
    /// release. Accepting it here would move the failure from the first
    /// configuration read to the first attachment somebody tried to release.
    /// </summary>
    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-1.00:00:00")]
    public void Startup_refuses_a_release_validity_that_no_grant_could_satisfy(string validity)
    {
        OptionsValidationException failure = Should.Throw<OptionsValidationException>(
            () => Validate(new Dictionary<string, string?>
            {
                ["Modules:AttachmentManagement:Validation:ReleaseValidity"] = validity,
            }));

        failure.Failures.ShouldContain(message =>
            message.Contains("maior que zero", StringComparison.Ordinal));
    }

    /// <summary>
    /// A duration the deadline arithmetic cannot hold is refused, and the
    /// ceiling is read from the type instead of written here: a number written
    /// here would be a limit on how long an operator may keep a release usable,
    /// which is not a limit this module gets to invent.
    /// </summary>
    [Fact]
    public void Startup_refuses_a_release_validity_the_deadline_arithmetic_cannot_hold()
    {
        OptionsValidationException failure = Should.Throw<OptionsValidationException>(
            () => Validate(new Dictionary<string, string?>
            {
                ["Modules:AttachmentManagement:Validation:ReleaseValidity"] =
                    TimeSpan.MaxValue.ToString(),
            }));

        failure.Failures.ShouldContain(message =>
            message.Contains("aritmética do vencimento", StringComparison.Ordinal));
    }

    /// <summary>
    /// A century is far past anything this product would choose and still
    /// inside the arithmetic, so the guard above refuses an overflow and never
    /// a duration. Without this, the same green would follow from a ceiling
    /// that refused everything.
    /// </summary>
    [Fact]
    public void Startup_accepts_a_release_validity_far_longer_than_the_decided_one()
        => Validate(new Dictionary<string, string?>
        {
            ["Modules:AttachmentManagement:Validation:ReleaseValidity"] = "36500.00:00:00",
        });

    [Fact]
    public void Startup_refuses_an_inconclusive_window_the_deadline_arithmetic_cannot_hold()
    {
        OptionsValidationException failure = Should.Throw<OptionsValidationException>(
            () => Validate(new Dictionary<string, string?>
            {
                ["Modules:AttachmentManagement:Validation:InconclusiveWindow"] =
                    TimeSpan.MaxValue.ToString(),
            }));

        failure.Failures.ShouldContain(message =>
            message.Contains("aritmética do prazo", StringComparison.Ordinal));
    }

    [Fact]
    public void Startup_refuses_a_negative_inconclusive_window()
    {
        OptionsValidationException failure = Should.Throw<OptionsValidationException>(
            () => Validate(new Dictionary<string, string?>
            {
                ["Modules:AttachmentManagement:Validation:InconclusiveWindow"] = "-00:00:01",
            }));

        failure.Failures.ShouldContain(message =>
            message.Contains("não pode ser negativa", StringComparison.Ordinal));
    }

    /// <summary>
    /// Zero is accepted because it closes: a wait that starts already over ends
    /// on the next validation, which is the safe direction.
    /// </summary>
    [Fact]
    public void Startup_accepts_an_inconclusive_window_of_zero()
        => Validate(new Dictionary<string, string?>
        {
            ["Modules:AttachmentManagement:Validation:InconclusiveWindow"] = "00:00:00",
        });

    /// <summary>
    /// The configuration file of the host, located from the build output. A
    /// path that stopped resolving would leave every reading above answering
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

    private static AttachmentValidationOptions Resolve(Dictionary<string, string?> values)
    {
        using ServiceProvider provider = Provider(values);
        return provider.GetRequiredService<IOptions<AttachmentValidationOptions>>().Value;
    }

    private static ServiceProvider Provider(Dictionary<string, string?> values)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAttachmentValidation(configuration);
        return services.BuildServiceProvider();
    }
}
