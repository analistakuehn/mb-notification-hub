using System.ComponentModel.DataAnnotations;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Fcm;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Twilio;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Resilience;

namespace NotificationHub.UnitTests.Dispatch;

/// <summary>
/// A range attribute on a nested options object is never evaluated by the
/// registration of the object that holds it, so every one of these limits read
/// as enforced in the source while an out-of-range value reached the control at
/// runtime. Each test here fails against the registration alone and passes only
/// because the holder validates what it nests.
/// </summary>
public sealed class NestedOptionsValidationTests
{
    /// <summary>
    /// The dictionary value is the case the framework reaches least: the ranges
    /// sit on the entry, and nothing walks into it. A contracted rate of zero
    /// would have booted, and the limiter fails open when the store misbehaves,
    /// so the control an operator believes is in force would stop existing
    /// without saying so.
    /// </summary>
    [Fact]
    public void A_contracted_rate_outside_its_range_is_refused_with_the_entry_named()
    {
        IReadOnlyList<ValidationResult> results = Validate(new ProviderRateLimitOptions
        {
            PerProvider = { ["twilio"] = new ProviderRateLimit { PermitsPerSecond = 0 } },
        });

        results.ShouldNotBeEmpty(
            "um limite contratado de zero chegava ao script do bucket em runtime, "
            + "porque a anotação de faixa numa entrada de dicionário nunca é avaliada.");
        results.SelectMany(result => result.MemberNames).ShouldContain(
            "PerProvider:twilio:PermitsPerSecond",
            "a mensagem tem de nomear a entrada, senão o operador não sabe qual corrigir.");
    }

    [Fact]
    public void A_rate_without_a_provider_key_is_refused()
        => Validate(new ProviderRateLimitOptions
        {
            PerProvider = { ["  "] = new ProviderRateLimit { PermitsPerSecond = 10 } },
        }).ShouldNotBeEmpty();

    [Fact]
    public void A_contracted_rate_inside_its_range_boots()
        => Validate(new ProviderRateLimitOptions
        {
            PerProvider = { ["twilio"] = new ProviderRateLimit { PermitsPerSecond = 10, BurstSeconds = 2 } },
        }).ShouldBeEmpty();

    /// <summary>
    /// The breaker knobs are nested on all three provider option types, so all
    /// three carried the same dead ranges. A sampling window of zero is the
    /// clearest case: it is a window the strategy cannot sample over.
    /// </summary>
    [Fact]
    public void Every_provider_refuses_breaker_knobs_outside_their_range()
    {
        var broken = new ProviderCircuitBreakerOptions { SamplingDurationSeconds = 0 };

        Validate(new TwilioOptions { CircuitBreaker = broken }).ShouldNotBeEmpty();
        Validate(new SendGridOptions { CircuitBreaker = broken }).ShouldNotBeEmpty();
        Validate(new FcmOptions { CircuitBreaker = broken }).ShouldNotBeEmpty();

        Validate(new TwilioOptions()).ShouldBeEmpty();
        Validate(new SendGridOptions()).ShouldBeEmpty();
        Validate(new FcmOptions()).ShouldBeEmpty();
    }

    [Fact]
    public void The_breaker_failure_names_the_nested_property()
        => Validate(new TwilioOptions
        {
            CircuitBreaker = new ProviderCircuitBreakerOptions { FailureRatio = 0 },
        })
            .SelectMany(result => result.MemberNames)
            .ShouldContain("CircuitBreaker:FailureRatio");

    /// <summary>
    /// The defect itself, on a holder that does not validate what it nests:
    /// the framework walks the properties of the object it was given, sees a
    /// reference, and stops. Without this the tests above would be asserting
    /// that a guard works without ever showing it was needed.
    /// </summary>
    [Fact]
    public void The_framework_alone_never_reaches_a_nested_range()
    {
        var outer = new HolderThatDoesNotValidateWhatItNests
        {
            CircuitBreaker = new ProviderCircuitBreakerOptions { SamplingDurationSeconds = 0 },
        };

        List<ValidationResult> results = [];
        Validator.TryValidateObject(
                outer,
                new ValidationContext(outer),
                results,
                validateAllProperties: true)
            .ShouldBeTrue(
                "se o framework alcançasse a faixa aninhada, os tipos de opções não "
                + "precisariam validar o que aninham e este conjunto de testes não "
                + "guardaria defeito nenhum.");
        results.ShouldBeEmpty();
    }

    private static IReadOnlyList<ValidationResult> Validate(IValidatableObject options)
        => [.. options.Validate(new ValidationContext(options))];

    private sealed class HolderThatDoesNotValidateWhatItNests
    {
        public ProviderCircuitBreakerOptions CircuitBreaker { get; init; } = new();
    }
}
