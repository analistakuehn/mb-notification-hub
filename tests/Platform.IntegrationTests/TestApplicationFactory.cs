using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace NotificationHub.IntegrationTests;

public sealed class TestApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
            ["Modules:Audit:Persistence:Ef:ConnectionString"] = "Host=localhost;Database=integration_tests;Username=test",
            ["Modules:TemplateManagement:Cache:Redis:ConnectionString"] = "localhost:6379",
            ["Modules:TemplateManagement:Cache:Redis:InstanceName"] = "integration-tests:",
            ["Modules:TemplateManagement:Persistence:Ef:ConnectionString"] = "Host=localhost;Database=integration_tests;Username=test",
            });
        });
    }
}
