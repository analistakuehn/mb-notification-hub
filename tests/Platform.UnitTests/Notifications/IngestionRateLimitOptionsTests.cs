using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Notifications.Infrastructure.RateLimiting;

namespace NotificationHub.UnitTests.Notifications;

/// <summary>
/// The ranges on a counting window sit on a dictionary value and on a list
/// item, which the registration of the holder never walks into, so every one
/// of these limits read as enforced in the source while an out-of-range value
/// booted the host and disabled the control at runtime. Each test here fails
/// against the registration alone and passes only because the holder
/// validates what it nests.
/// </summary>
public sealed class IngestionRateLimitOptionsTests
{
    /// <summary>
    /// A permit limit of zero is the clearest case: it is a budget no request
    /// can spend, so the window it belongs to stops rejecting anything. The
    /// limiter also fails open on a Redis fault, so nothing downstream would
    /// have told an operator the budget was gone.
    /// </summary>
    [Fact]
    public void A_principal_permit_limit_outside_its_range_is_refused_with_the_entry_named()
    {
        IReadOnlyList<ValidationResult> results = Validate(new IngestionRateLimitOptions
        {
            PerPrincipal =
            {
                ["critical"] = new RateWindow { PermitLimit = 0, WindowSeconds = 1 },
            },
        });

        results.ShouldNotBeEmpty(
            "um limite de zero subia o host com o controle desligado, "
            + "porque a anotação de faixa num valor de dicionário nunca é avaliada.");
        results.SelectMany(result => result.MemberNames).ShouldContain(
            "PerPrincipal:critical:PermitLimit",
            "a mensagem tem de nomear a entrada, senão o operador não sabe qual corrigir.");
    }

    [Fact]
    public void A_principal_window_longer_than_a_week_is_refused()
        => Validate(new IngestionRateLimitOptions
            {
                PerPrincipal =
                {
                    ["critical"] = new RateWindow { PermitLimit = 50, WindowSeconds = 604_801 },
                },
            })
            .SelectMany(result => result.MemberNames)
            .ShouldContain("PerPrincipal:critical:WindowSeconds");

    /// <summary>
    /// The recipient dimension nests one level deeper, so the index has to
    /// reach the message too: the windows of a class are cumulative and an
    /// operator reading only the class name would not know which of them
    /// carries the bad value.
    /// </summary>
    [Fact]
    public void A_recipient_window_outside_its_range_is_refused_with_its_index_named()
    {
        IReadOnlyList<ValidationResult> results = Validate(new IngestionRateLimitOptions
        {
            PerRecipient =
            {
                ["critical"] =
                [
                    new RateWindow { PermitLimit = 5, WindowSeconds = 600 },
                    new RateWindow { PermitLimit = -1, WindowSeconds = 86_400 },
                ],
            },
        });

        results.SelectMany(result => result.MemberNames).ShouldContain(
            "PerRecipient:critical:1:PermitLimit",
            "sem o índice o operador não sabe qual das janelas cumulativas corrigir.");
    }

    [Fact]
    public void A_window_without_a_class_key_is_refused_in_both_dimensions()
    {
        Validate(new IngestionRateLimitOptions
        {
            PerPrincipal = { ["  "] = new RateWindow { PermitLimit = 50, WindowSeconds = 1 } },
        }).ShouldNotBeEmpty();

        Validate(new IngestionRateLimitOptions
        {
            PerRecipient = { [""] = [new RateWindow { PermitLimit = 5, WindowSeconds = 600 }] },
        }).ShouldNotBeEmpty();
    }

    [Fact]
    public void Windows_inside_their_ranges_boot()
        => Validate(new IngestionRateLimitOptions
        {
            PerPrincipal =
            {
                ["critical"] = new RateWindow { PermitLimit = 50, WindowSeconds = 1 },
                ["operational"] = new RateWindow { PermitLimit = 20, WindowSeconds = 1 },
            },
            PerRecipient =
            {
                ["critical"] =
                [
                    new RateWindow { PermitLimit = 5, WindowSeconds = 600 },
                    new RateWindow { PermitLimit = 20, WindowSeconds = 86_400 },
                ],
            },
        }).ShouldBeEmpty();

    /// <summary>
    /// A class with no window configured has no limit in that dimension, which
    /// is the shipped shape of the recipient map: only the critical class
    /// carries budgets. Refusing the absence would turn every unlisted class
    /// into a boot failure.
    /// </summary>
    [Fact]
    public void An_unconfigured_map_boots()
        => Validate(new IngestionRateLimitOptions()).ShouldBeEmpty();

    /// <summary>
    /// The guard only matters if the registration reaches it, so this walks
    /// the real path of an operator: the configuration keys, the binder, and
    /// the same <c>ValidateDataAnnotations</c> both hosts register. Asserting
    /// on <c>Validate</c> alone would leave the wiring untested.
    /// </summary>
    [Fact]
    public void The_registration_refuses_the_out_of_range_value_at_resolution()
    {
        OptionsValidationException exception = Should.Throw<OptionsValidationException>(
            () => ResolveOptions(new Dictionary<string, string?>
            {
                ["Modules:Notifications:RateLimits:PerRecipient:transactional:0:PermitLimit"] = "0",
                ["Modules:Notifications:RateLimits:PerRecipient:transactional:0:WindowSeconds"] = "60",
            }));

        exception.Failures.ShouldContain(failure =>
            failure.Contains("PerRecipient:transactional:0:PermitLimit", StringComparison.Ordinal));
    }

    [Fact]
    public void The_registration_accepts_the_configuration_the_deployment_ships()
    {
        IngestionRateLimitOptions options = ResolveOptions(new Dictionary<string, string?>
        {
            ["Modules:Notifications:RateLimits:PerPrincipal:critical:PermitLimit"] = "50",
            ["Modules:Notifications:RateLimits:PerPrincipal:critical:WindowSeconds"] = "1",
            ["Modules:Notifications:RateLimits:PerRecipient:critical:0:PermitLimit"] = "5",
            ["Modules:Notifications:RateLimits:PerRecipient:critical:0:WindowSeconds"] = "600",
        });

        options.PerPrincipal["critical"].PermitLimit.ShouldBe(50);
        options.PerRecipient["critical"].Count.ShouldBe(1);
    }

    /// <summary>
    /// The defect itself, on a holder that does not validate what it nests:
    /// the framework walks the properties of the object it was given, sees a
    /// dictionary reference, and stops. Without this the tests above would be
    /// asserting that a guard works without ever showing it was needed.
    /// </summary>
    [Fact]
    public void The_framework_alone_never_reaches_a_nested_range()
    {
        var outer = new HolderThatDoesNotValidateWhatItNests
        {
            PerPrincipal = { ["critical"] = new RateWindow { PermitLimit = 0, WindowSeconds = 0 } },
            PerRecipient = { ["critical"] = [new RateWindow { PermitLimit = -1, WindowSeconds = -1 }] },
        };

        List<ValidationResult> results = [];
        Validator.TryValidateObject(
                outer,
                new ValidationContext(outer),
                results,
                validateAllProperties: true)
            .ShouldBeTrue(
                "se o framework alcançasse a faixa aninhada, este tipo de opções não "
                + "precisaria validar o que aninha e este conjunto de testes não "
                + "guardaria defeito nenhum.");
        results.ShouldBeEmpty();
    }

    private static IReadOnlyList<ValidationResult> Validate(IngestionRateLimitOptions options)
        => [.. options.Validate(new ValidationContext(options))];

    // Mirrors the registration both NotificationsModule and
    // KafkaIngressWorkerRole declare. Calling either one instead would drag in
    // Redis, the database and the broker for a check about configuration.
    private static IngestionRateLimitOptions ResolveOptions(Dictionary<string, string?> values)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<IngestionRateLimitOptions>()
            .Bind(configuration.GetSection(IngestionRateLimitOptions.SectionName))
            .ValidateDataAnnotations();

        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<IngestionRateLimitOptions>>().Value;
    }

    private sealed class HolderThatDoesNotValidateWhatItNests
    {
        public Dictionary<string, RateWindow> PerPrincipal { get; init; } = [];

        public Dictionary<string, List<RateWindow>> PerRecipient { get; init; } = [];
    }
}
