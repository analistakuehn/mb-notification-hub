using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Audit.Infrastructure.AuditTrail;
using NotificationHub.Api.Modules.Audit.Infrastructure.Export;
using NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Verification;

/// <summary>
/// Composition of the periodic chain verification and of the health check that
/// exposes it. The check is registered wherever the verification runs: a
/// sensor nobody watches proves nothing.
/// </summary>
internal static class ChainVerificationSetup
{
    internal static IServiceCollection AddAuditChainVerification(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ChainVerificationOptions>()
            .Bind(configuration.GetSection(ChainVerificationOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => options.Interval >= TimeSpan.FromMinutes(1),
                "A cadência da verificação de cadeia deve ser de pelo menos um minuto.")
            .Validate(
                options => options.Interval <= TimeSpan.FromDays(1),
                "A cadência da verificação de cadeia deve ser de no máximo um dia; acima disso a adulteração passa despercebida por tempo demais.")
            .Validate(
                options => options.StabilizationWatermark >= TimeSpan.Zero,
                "O watermark de estabilização da verificação não pode ser negativo.")
            .Validate(
                options => options.FullVerificationInterval >= options.Interval,
                "A cadência da verificação integral deve ser maior ou igual à cadência da verificação incremental.")
            .ValidateOnStart();

        // Registered defensively: this setup composes on its own, whichever
        // other setup of the module a host also happens to call.
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAuditTrail, TransactionalAuditTrail>();
        services.TryAddScoped<AuditTrailReader>();
        services.TryAddScoped<AuditPartitionCatalog>();
        services.TryAddScoped<AuditMaintenanceLock>();
        services.TryAddScoped<AuditMaintenanceJournal>();
        services.AddScoped<ChainVerifier>();
        services.AddScoped<ChainVerificationRound>();
        services.AddHostedService<ChainVerificationService>();
        services.AddHealthChecks().Add(new HealthCheckRegistration(
            "audit-chain-verification",
            serviceProvider => new ChainVerificationHealthCheck(
                serviceProvider.GetRequiredService<AuditDbContext>(),
                StaleAfter(serviceProvider),
                serviceProvider.GetRequiredService<TimeProvider>()),
            failureStatus: null,
            tags: null));
        return services;
    }

    /// <summary>
    /// Two cadences of tolerance: one missed round is a hiccup, two in a row
    /// means the sensor stopped and the silence no longer means anything.
    /// </summary>
    private static TimeSpan StaleAfter(IServiceProvider serviceProvider)
        => serviceProvider.GetRequiredService<IOptions<ChainVerificationOptions>>().Value.Interval * 2;
}
