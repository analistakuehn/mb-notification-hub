using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.Dispatch;

/// <summary>
/// Exercises the webhook interpreters through the composition the host really
/// builds, because the value of this surface is that a running process can
/// pick the right interpreter for an inbound key without a deploy-time list.
/// </summary>
public sealed class ProviderWebhookInterpreterResolutionTests(TestApplicationFactory factory)
    : IClassFixture<TestApplicationFactory>
{
    // Signature vector published by the provider for this exact request; the
    // only thing it proves here is that the configured section reaches the
    // interpreter, since the recipe itself is pinned by the unit suite.
    private const string VectorUrl = "https://mycompany.com/myapp.php?foo=1&bar=2";
    private const string VectorToken = "12345";
    private const string VectorSignature = "RSOYDt4T1cUTdK1PDd93/VVr8B8=";

    private const string VectorBody =
        "CallSid=CA1234567890ABCDE&Caller=%2B14158675309&Digits=1234"
        + "&From=%2B14158675309&To=%2B18005551212";

    [Fact]
    public void The_host_resolves_one_interpreter_for_every_provider_it_dispatches_through()
    {
        IProviderWebhookInterpreterResolver resolver =
            factory.Services.GetRequiredService<IProviderWebhookInterpreterResolver>();

        Result<IProviderWebhookInterpreter> twilio = resolver.Resolve("twilio");
        Result<IProviderWebhookInterpreter> sendgrid = resolver.Resolve("sendgrid");

        twilio.IsSuccess.ShouldBeTrue(twilio.Error);
        twilio.Value!.ProviderKey.ShouldBe("twilio");
        sendgrid.IsSuccess.ShouldBeTrue(sendgrid.Error);
        sendgrid.Value!.ProviderKey.ShouldBe("sendgrid");
    }

    [Fact]
    public void The_host_refuses_a_provider_key_it_hosts_no_interpreter_for()
    {
        IProviderWebhookInterpreterResolver resolver =
            factory.Services.GetRequiredService<IProviderWebhookInterpreterResolver>();

        Result<IProviderWebhookInterpreter> result = resolver.Resolve("acme-mail");

        ProviderWebhookRefusal.Is(result, ProviderWebhookRefusal.ProviderUnknown).ShouldBeTrue(result.Error);
    }

    [Fact]
    public void A_host_without_verification_secrets_still_starts_and_refuses_at_use_time()
    {
        IProviderWebhookInterpreterResolver resolver =
            factory.Services.GetRequiredService<IProviderWebhookInterpreterResolver>();
        Result<IProviderWebhookInterpreter> twilio = resolver.Resolve("twilio");

        Result<VerifiedProviderWebhook> result = twilio.Value!.Verify(TwilioVectorRequest());

        ProviderWebhookRefusal.Is(result, ProviderWebhookRefusal.SignatureInvalid).ShouldBeTrue(result.Error);
    }

    [Fact]
    public void The_configured_section_reaches_the_interpreter_that_reads_it()
    {
        using ServiceProvider services = DispatchTestServices.BuildWebhookHost(
            new Dictionary<string, string?>
            {
                ["Modules:Dispatch:Webhooks:Twilio:AuthToken"] = VectorToken,
            });
        IProviderWebhookInterpreterResolver resolver =
            services.GetRequiredService<IProviderWebhookInterpreterResolver>();
        Result<IProviderWebhookInterpreter> twilio = resolver.Resolve("twilio");

        Result<VerifiedProviderWebhook> result = twilio.Value!.Verify(TwilioVectorRequest());

        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    [Fact]
    public void A_configured_origin_allowlist_reaches_the_interpreter_that_enforces_it()
    {
        using ServiceProvider services = DispatchTestServices.BuildWebhookHost(
            new Dictionary<string, string?>
            {
                ["Modules:Dispatch:Webhooks:Twilio:AuthToken"] = VectorToken,
                ["Modules:Dispatch:Webhooks:Twilio:AllowedNetworks:0"] = "54.172.60.0/24",
            });
        IProviderWebhookInterpreterResolver resolver =
            services.GetRequiredService<IProviderWebhookInterpreterResolver>();
        Result<IProviderWebhookInterpreter> twilio = resolver.Resolve("twilio");

        Result<VerifiedProviderWebhook> allowed =
            twilio.Value!.Verify(TwilioVectorRequest("54.172.60.3"));
        Result<VerifiedProviderWebhook> refused =
            twilio.Value.Verify(TwilioVectorRequest("203.0.113.9"));

        allowed.IsSuccess.ShouldBeTrue(allowed.Error);
        ProviderWebhookRefusal.Is(refused, ProviderWebhookRefusal.OriginNotAllowed)
            .ShouldBeTrue(refused.Error);
    }

    [Fact]
    public void A_configured_vocabulary_replaces_the_shipped_one_instead_of_extending_it()
    {
        using ServiceProvider services = DispatchTestServices.BuildWebhookHost(
            new Dictionary<string, string?>
            {
                ["Modules:Dispatch:Webhooks:SendGrid:HardBounceCodes:0"] = "blocked",
            });
        IProviderWebhookInterpreterResolver resolver =
            services.GetRequiredService<IProviderWebhookInterpreterResolver>();
        Result<IProviderWebhookInterpreter> sendgrid = resolver.Resolve("sendgrid");

        Result<IReadOnlyList<ProviderDeliveryEvent>> configured = sendgrid.Value!.Interpret(
            Verified("""[{"sg_event_id":"evt-s","event":"bounce","type":"blocked"}]"""));
        Result<IReadOnlyList<ProviderDeliveryEvent>> shipped = sendgrid.Value.Interpret(
            Verified("""[{"sg_event_id":"evt-b","event":"bounce","type":"bounce"}]"""));

        configured.Value![0].Signal.ShouldBe(SuppressionSignal.HardBounce);
        shipped.Value![0].Signal.ShouldBe(SuppressionSignal.None);
    }

    private static VerifiedProviderWebhook Verified(string body)
        => new("sendgrid", DateTimeOffset.UnixEpoch, Encoding.UTF8.GetBytes(body));

    private static ProviderWebhookRequest TwilioVectorRequest(string? remoteIpAddress = null)
        => new(
            "twilio",
            VectorUrl,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["X-Twilio-Signature"] = VectorSignature,
            },
            remoteIpAddress,
            Encoding.UTF8.GetBytes(VectorBody));
}
