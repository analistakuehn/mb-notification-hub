using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace NotificationHub.IntegrationTests;

/// <summary>
/// The committed development signing key must never be accepted outside the
/// Development environment: anyone with repository access could forge tokens
/// that the host would trust.
/// </summary>
public sealed class DevSigningKeyGuardTests
{
    private const string DevIssuer = "notification-hub-dev-only";

    [Fact]
    public void The_host_refuses_to_boot_outside_development_with_the_dev_signing_key()
    {
        using var factory = new GuardedHostFactory("Production");

        InvalidOperationException exception =
            Should.Throw<InvalidOperationException>(() => factory.CreateClient());

        exception.Message.ShouldContain(DevIssuer);
        exception.Message.ShouldContain("Production");
    }

    [Fact]
    public void The_host_boots_in_development_with_the_dev_signing_key()
    {
        using var factory = new GuardedHostFactory("Development");

        HttpClient client = factory.CreateClient();

        client.ShouldNotBeNull();
    }

    private sealed class GuardedHostFactory(string environment) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authentication:Schemes:Bearer:ValidIssuer"] = DevIssuer,
                    ["Authentication:Schemes:Bearer:ValidAudiences:0"] = "notification-hub",
                    ["Authentication:Schemes:Bearer:SigningKeys:0:Issuer"] = DevIssuer,
                    ["Authentication:Schemes:Bearer:SigningKeys:0:Value"] =
                        "ZGV2LW9ubHkgc2lnbmluZyBrZXkgLSBuZXZlciB1c2Ugb3V0c2lkZSBsb2NhbGhvc3Q=",
                    ["Modules:TemplateManagement:Cache:Redis:ConnectionString"] = "localhost:6379",
                    ["Modules:TemplateManagement:Cache:Redis:InstanceName"] = "integration-tests:",
                    ["Modules:TemplateManagement:Persistence:Ef:ConnectionString"] =
                        "Host=localhost;Database=integration_tests;Username=test",
                }));
        }
    }
}
