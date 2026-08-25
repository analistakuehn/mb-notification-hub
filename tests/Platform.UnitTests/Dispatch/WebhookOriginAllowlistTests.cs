using System.ComponentModel.DataAnnotations;
using System.Net;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Twilio;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Webhooks;

namespace NotificationHub.UnitTests.Dispatch;

/// <summary>
/// The origin guard compares networks, not text. A textual prefix is not a
/// range, and the difference is not cosmetic: it decides who may write delivery
/// evidence and trigger suppression on this hub.
/// </summary>
public sealed class WebhookOriginAllowlistTests
{
    /// <summary>
    /// The defect a textual comparison carried. As a string, <c>54.172.6</c>
    /// is a prefix of every address from <c>54.172.60.x</c> through
    /// <c>54.172.69.x</c>, so an operator who pinned one network silently
    /// authorised ten.
    /// </summary>
    [Fact]
    public void A_network_does_not_authorise_the_neighbours_its_text_prefixes()
    {
        WebhookRequestGuards.TryParseNetworks(["54.172.6.0/24"], out IPNetwork[] allowed, out _)
            .ShouldBeTrue();

        WebhookRequestGuards.IsOriginAllowed("54.172.6.7", allowed).ShouldBeTrue();
        WebhookRequestGuards.IsOriginAllowed("54.172.69.7", allowed).ShouldBeFalse(
            "a comparação textual autorizava esta origem, que está fora da rede listada: "
            + "quem pinou uma rede acabava autorizando dez.");

        // The reading this replaced, spelled out on the same input, because the
        // signature changed with it and there is no way to run the old guard
        // here: the two readings disagree exactly where it matters, and that
        // disagreement is the defect.
        "54.172.69.7".StartsWith("54.172.6", StringComparison.Ordinal).ShouldBeTrue(
            "se o prefixo textual não casasse esta origem, o defeito que este teste "
            + "guarda nunca teria existido e a asserção acima não provaria nada.");
    }

    /// <summary>
    /// The same address in its mapped form is the same host. A dual stack
    /// listener hands the guard the mapped spelling, and a list written in the
    /// obvious form would otherwise refuse every authentic callback.
    /// </summary>
    [Fact]
    public void An_ipv4_address_in_its_mapped_form_is_compared_as_ipv4()
    {
        WebhookRequestGuards.TryParseNetworks(["54.172.60.0/24"], out IPNetwork[] allowed, out _)
            .ShouldBeTrue();

        WebhookRequestGuards.IsOriginAllowed("::ffff:54.172.60.1", allowed).ShouldBeTrue(
            "a comparação textual recusava a forma mapeada e reportava a origem como forjada.");
        WebhookRequestGuards.IsOriginAllowed("::ffff:54.172.61.1", allowed).ShouldBeFalse();
    }

    [Fact]
    public void An_empty_allowlist_is_the_allowlist_turned_off()
        => WebhookRequestGuards.IsOriginAllowed("203.0.113.9", []).ShouldBeTrue();

    /// <summary>
    /// A configured list with no matching network refuses, and so does an
    /// address nobody can parse: neither is a pass.
    /// </summary>
    [Theory]
    [InlineData("203.0.113.9")]
    [InlineData("not-an-address")]
    [InlineData("")]
    [InlineData(null)]
    public void A_configured_list_refuses_whatever_it_does_not_name(string? origin)
    {
        WebhookRequestGuards.TryParseNetworks(["54.172.60.0/24"], out IPNetwork[] allowed, out _)
            .ShouldBeTrue();

        WebhookRequestGuards.IsOriginAllowed(origin, allowed).ShouldBeFalse();
    }

    [Fact]
    public void Parsing_names_the_value_that_is_not_a_network()
    {
        WebhookRequestGuards.TryParseNetworks(["54.172.60.0/24", "54.172.60."], out _, out var invalid)
            .ShouldBeFalse();

        invalid.ShouldBe(
            "54.172.60.",
            "o valor recusado tem de ser nomeado, senão o operador não sabe qual das "
            + "entradas corrigir.");
    }

    /// <summary>
    /// Both providers refuse an unparseable range at host start. Left to
    /// verification time the failure is silent in the worst direction: an entry
    /// nobody parsed is an entry that never matches, so the guard would refuse
    /// authentic traffic and file each refusal as an attempted forgery.
    /// </summary>
    [Fact]
    public void Both_providers_refuse_a_range_that_is_not_a_network_at_startup()
    {
        Validate(new TwilioWebhookOptions { AllowedNetworks = ["54.172.60."] })
            .ShouldNotBeEmpty();
        Validate(new SendGridWebhookOptions { AllowedNetworks = ["168.245."] })
            .ShouldNotBeEmpty();

        Validate(new TwilioWebhookOptions { AllowedNetworks = ["54.172.60.0/24"] }).ShouldBeEmpty();
        Validate(new SendGridWebhookOptions { AllowedNetworks = ["168.245.0.0/16"] }).ShouldBeEmpty();
        Validate(new TwilioWebhookOptions()).ShouldBeEmpty();
        Validate(new SendGridWebhookOptions()).ShouldBeEmpty();
    }

    private static IReadOnlyList<ValidationResult> Validate(IValidatableObject options)
        => [.. options.Validate(new ValidationContext(options))];
}
