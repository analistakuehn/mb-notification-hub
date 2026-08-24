using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;

namespace NotificationHub.IntegrationTests.Dispatch;

public sealed class LiveProviderSmokeTests
{
    private const string LiveEmailVariable = "NOTIFICATION_HUB_LIVE_EMAIL";
    private const string LiveSmsVariable = "NOTIFICATION_HUB_LIVE_SMS";
    private const string UserSecretsId = "4f0f3676-a1b6-5a57-9b8c-b9c6b6517ec9";

    [RequiresLiveProvidersFact]
    public async Task Sends_a_live_email_through_the_sendgrid_adapter()
    {
        var destination = RequiredEnvironmentValue(LiveEmailVariable);
        await using ServiceProvider services = BuildProviderHost();
        IChannelProvider provider = DispatchTestServices.ResolveProviderByKey(services, "sendgrid");
        var request = new DispatchRequest(
            new EmailDeliveryTarget(destination),
            new EmailMessage(
                "Teste do MonteBravo Notification Hub",
                "Validação de entrega",
                "<p>Este e-mail confirma o teste real do adapter SendGrid pelo Notification Hub.</p>",
                "Este e-mail confirma o teste real do adapter SendGrid pelo Notification Hub."));

        ProviderResult result = await provider.SendAsync(request, CancellationToken.None);

        result.Outcome.ShouldBe(
            ProviderOutcome.Accepted,
            $"SendGrid retornou {result.ErrorCode}: {result.ErrorMessage}");
    }

    [RequiresLiveProvidersFact]
    public async Task Sends_a_live_verification_sms_through_the_twilio_adapter()
    {
        var destination = RequiredEnvironmentValue(LiveSmsVariable);
        await using ServiceProvider services = BuildProviderHost();
        IChannelProvider provider = DispatchTestServices.ResolveProviderByKey(services, "twilio");
        var request = new DispatchRequest(
            new SmsDeliveryTarget(destination),
            new SmsMessage("482731"));

        ProviderResult result = await provider.SendAsync(request, CancellationToken.None);

        result.Outcome.ShouldBe(
            ProviderOutcome.Accepted,
            $"Twilio retornou {result.ErrorCode}: {result.ErrorMessage}");
        result.ProviderMessageId.ShouldNotBeNullOrWhiteSpace();
    }

    private static ServiceProvider BuildProviderHost()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var secretsPath = Path.Combine(
            appData,
            "Microsoft",
            "UserSecrets",
            UserSecretsId,
            "secrets.json");
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonFile(secretsPath, optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:Dispatch:Providers:SendGrid:SandboxMode"] = "false",
            })
            .Build();

        return DispatchTestServices.BuildProviderHost(configuration.AsEnumerable());
    }

    private static string RequiredEnvironmentValue(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"A variável obrigatória '{name}' não foi configurada.")
            : value;
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresLiveProvidersFactAttribute : FactAttribute
{
    public RequiresLiveProvidersFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("NOTIFICATION_HUB_LIVE_PROVIDER_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Live provider tests are opt-in because they send real e-mail and SMS messages.";
        }
    }
}
