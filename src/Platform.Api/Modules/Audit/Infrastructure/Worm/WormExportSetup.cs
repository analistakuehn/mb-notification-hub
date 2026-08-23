using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Modules.Audit.Infrastructure.AuditTrail;
using NotificationHub.Api.Modules.Audit.Infrastructure.Export;
using NotificationHub.Api.Modules.Audit.Infrastructure.Verification;
using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Worm;

/// <summary>
/// Composition of the WORM export: the immutable store, the exporter, the
/// planner of daily slices, the authoritative closing export, and the
/// verifier that reads the copy back. The attestation signer comes from the
/// platform, because signing evidence is not specific to this module.
/// </summary>
internal static class WormExportSetup
{
    internal static IServiceCollection AddAuditWormExport(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<WormExportOptions>()
            .Bind(configuration.GetSection(WormExportOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => options.StabilizationDelay >= TimeSpan.Zero,
                "O atraso de estabilização do export diário não pode ser negativo.")
            .Validate(
                options => options.StabilizationDelay <= TimeSpan.FromDays(7),
                "O atraso de estabilização do export diário deve ser de no máximo sete dias; acima disso a evidência diária deixa de ser diária.")
            .ValidateOnStart();

        services.AddAttestationSigning(configuration);

        // Registered defensively: this setup composes on its own, whichever
        // other setup of the module a host also happens to call. It never
        // pulls the verification job in, because hosting a job is the role's
        // decision, not the exporter's.
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAuditTrail, TransactionalAuditTrail>();
        services.TryAddScoped<AuditTrailReader>();
        services.TryAddScoped<AuditMaintenanceJournal>();
        services.AddSingleton<IAmazonS3>(serviceProvider =>
            CreateS3Client(serviceProvider.GetRequiredService<IOptions<WormExportOptions>>().Value));
        services.TryAddScoped<IWormObjectStore, S3WormObjectStore>();
        services.AddScoped<AuditManifestStore>();
        services.AddScoped<AuditExporter>();
        services.AddScoped<AuditExportPlanner>();
        services.AddScoped<AuditClosingExporter>();
        services.AddScoped<WormExportVerifier>();
        return services;
    }

    /// <summary>
    /// Without static keys the SDK falls back to its default credential chain
    /// (instance profile, environment), which is the production path; the
    /// local emulator needs an explicit endpoint and path-style addressing.
    /// </summary>
    private static AmazonS3Client CreateS3Client(WormExportOptions options)
    {
        var config = new AmazonS3Config { ForcePathStyle = options.ForcePathStyle };
        if (options.ServiceUrl is not null)
        {
            config.ServiceURL = options.ServiceUrl;
            if (options.Region is not null)
            {
                config.AuthenticationRegion = options.Region;
            }
        }
        else if (options.Region is not null)
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        return options is { AccessKey: not null, SecretKey: not null }
            ? new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config)
            : new AmazonS3Client(config);
    }
}
