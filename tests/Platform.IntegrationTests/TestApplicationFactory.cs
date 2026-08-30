using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;

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
            ["Modules:TemplateManagement:Persistence:Ef:ConnectionString"] = "Host=localhost;Database=integration_tests;Username=test",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IKillSwitchSnapshotSource>();
            services.AddSingleton<IKillSwitchSnapshotSource, EmptyKillSwitchSnapshotSource>();
        });
    }

    private sealed class EmptyKillSwitchSnapshotSource : IKillSwitchSnapshotSource
    {
        public Task<IReadOnlySet<KillSwitchAddress>> LoadActiveAsync(
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            HashSet<KillSwitchAddress> active = [];
            return Task.FromResult<IReadOnlySet<KillSwitchAddress>>(active);
        }
    }
}
