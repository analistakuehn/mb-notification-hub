using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NotificationHub.Api.Modules.Audit;

namespace NotificationHub.IntegrationTests.Audit;

/// <summary>
/// Builds the composition of the audit-maintenance worker role against real
/// containers, through the same public entry the worker host uses. Tests tune
/// one instance with configuration overrides and, when a test needs to observe
/// or disturb a collaborator, with an explicit service override; nothing here
/// reaches into the module's internals to assemble the graph by hand.
/// </summary>
internal static class AuditMaintenanceComposition
{
    /// <summary>Committed development signing key of the tests: a fixed P-256 pair, never a production key.</summary>
    internal const string TestKeyId = "attestation-tests-dev-only";

    internal static ServiceProvider Build(
        string postgresConnectionString,
        IDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configureServices = null,
        ILoggerProvider? loggerProvider = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Modules:Audit:Persistence:Ef:ConnectionString"] = postgresConnectionString,
            ["Modules:Notifications:Persistence:Ef:ConnectionString"] = postgresConnectionString,
            ["Modules:Audit:PartitionManager:Enabled"] = "false",
            ["Modules:Audit:ChainVerification:Enabled"] = "false",
            ["Modules:Audit:WormExport:Bucket"] = "audit-worm-tests",
            ["Platform:Cryptography:Attestation:Provider"] = "local",
            ["Platform:Cryptography:Attestation:KeyId"] = TestKeyId,
            ["Platform:Cryptography:Attestation:PrivateKey"] = AttestationTestKey.PrivateKeyBase64,
        };
        if (overrides is not null)
        {
            foreach ((var key, var value) in overrides)
            {
                settings[key] = value;
            }
        }

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            if (loggerProvider is not null)
            {
                logging.AddProvider(loggerProvider);
            }
        });
        AuditMaintenanceWorkerRole.ConfigureServices(services, configuration);
        configureServices?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
