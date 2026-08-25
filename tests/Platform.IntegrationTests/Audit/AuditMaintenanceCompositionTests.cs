using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Infrastructure.Verification;
using NotificationHub.Api.Modules.Audit.Infrastructure.Worm;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.Compliance.Features.Reporting;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.IntegrationTests.TemplateManagement;
using NotificationHub.Worker;

namespace NotificationHub.IntegrationTests.Audit;

/// <summary>
/// Where the maintenance jobs live. Acting on partitions is the job of one
/// role and of no other host, while seeing the coverage run out is everyone's
/// business.
/// </summary>
public sealed class AuditMaintenanceRoleCompositionTests
{
    [Fact]
    public void The_maintenance_role_hosts_the_partition_manager_the_verification_and_the_evidence_report()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(RoleSettings());
        builder.Services.AddLogging();

        WorkerRoleCatalog.Register(builder.Services, builder.Configuration);
        using IHost host = builder.Build();

        IHostedService[] hosted = [.. host.Services.GetServices<IHostedService>()];
        hosted.OfType<PartitionManagerService>().ShouldHaveSingleItem();
        hosted.OfType<ChainVerificationService>().ShouldHaveSingleItem();

        // The recurring evidence report rides in this role and in no other:
        // it is the singleton that already runs on a batch cadence and already
        // holds the immutable store the report lands in.
        hosted.OfType<MonthlyEvidenceReportService>().ShouldHaveSingleItem();
        hosted
            .Where(service => service is not PartitionManagerService
                and not ChainVerificationService
                and not MonthlyEvidenceReportService)
            .ShouldAllBe(service => service.GetType().Namespace!.StartsWith("Microsoft.", StringComparison.Ordinal));

        // Provisioning, closing cycle, export and verification resolve end to
        // end in this role, and nowhere else.
        using IServiceScope scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<PartitionMaintenanceRound>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<PartitionClosingCycle>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<ChainVerificationRound>().ShouldNotBeNull();

        // The three contracts the composer asks for resolve here, each from
        // the module that owns what it answers about.
        scope.ServiceProvider.GetRequiredService<IEvidenceArchive>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IAuditPeriodEvidence>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<INotificationOutcomeReport>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<ComposeMonthlyEvidence.Handler>().ShouldNotBeNull();

        HealthCheckRegistration[] checks = [.. host.Services
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations];
        checks.Select(check => check.Name).ShouldContain("audit-partitions");
        checks.Select(check => check.Name).ShouldContain("audit-chain-verification");
    }

    [Fact]
    public void The_role_refuses_to_boot_without_the_bucket_that_holds_the_evidence()
    {
        Dictionary<string, string?> settings = RoleSettings();
        settings.Remove("Modules:Audit:WormExport:Bucket");
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddLogging();
        WorkerRoleCatalog.Register(builder.Services, builder.Configuration);
        using IHost host = builder.Build();

        OptionsValidationException failure = Should.Throw<OptionsValidationException>(
            () => host.Services.GetRequiredService<IOptions<WormExportOptions>>().Value);

        failure.Failures.ShouldContain(message => message.Contains("Bucket", StringComparison.Ordinal));
    }

    private static Dictionary<string, string?> RoleSettings()
        => new()
        {
            ["Worker:Role"] = "audit-maintenance",
            ["Modules:Audit:Persistence:Ef:ConnectionString"] =
                "Host=localhost;Database=worker_tests;Username=test",
            ["Modules:Notifications:Persistence:Ef:ConnectionString"] =
                "Host=localhost;Database=worker_tests;Username=test",
            ["Modules:Audit:WormExport:Bucket"] = "worker-tests-worm",
            ["Platform:Cryptography:Attestation:Provider"] = "local",
            ["Platform:Cryptography:Attestation:KeyId"] = AuditMaintenanceComposition.TestKeyId,
            ["Platform:Cryptography:Attestation:PrivateKey"] = AttestationTestKey.PrivateKeyBase64,
        };
}

/// <summary>
/// The request-serving host after the maintenance moved out: it still watches
/// the partition coverage, and it no longer acts on partitions.
/// </summary>
[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class AuditApiHostCompositionTests(TemplateManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public void The_api_keeps_the_partition_coverage_check_and_hosts_no_maintenance_job()
    {
        IHostedService[] hosted = [.. fixture.Services.GetServices<IHostedService>()];
        hosted.OfType<PartitionManagerService>().ShouldBeEmpty();
        hosted.OfType<ChainVerificationService>().ShouldBeEmpty();
        hosted.OfType<MonthlyEvidenceReportService>().ShouldBeEmpty();

        var checks = fixture.Services
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations
            .Select(registration => registration.Name)
            .ToArray();

        checks.ShouldContain("audit-partitions");
        checks.ShouldNotContain("audit-chain-verification");
    }
}
