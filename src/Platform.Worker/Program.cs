using NotificationHub.Api.Infrastructure.Cryptography;

namespace NotificationHub.Worker;

/// <summary>
/// Thin composition root of the worker host: resolve the configured role,
/// compose what that role owns, run. A namespaced entry point on purpose, so
/// the type never collides with the API host's <c>Program</c> in test
/// projects that reference both hosts.
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        WorkerRoleCatalog.Register(builder.Services, builder.Configuration);
        IHost host = builder.Build();

        // Same containment rule as the API host for the committed development
        // envelope master key: it only ever derives data keys in Development.
        // Checked after Build() on purpose: only the built host sees the
        // final configuration, including deployment overlays.
        IConfiguration configuration = host.Services.GetRequiredService<IConfiguration>();
        IHostEnvironment environment = host.Services.GetRequiredService<IHostEnvironment>();
        var envelopeKeyId = configuration[$"{EnvelopeCipherOptions.SectionName}:KeyId"];
        if (envelopeKeyId is not null
            && envelopeKeyId.Contains(EnvelopeCipherOptions.DevelopmentKeyIdMarker, StringComparison.OrdinalIgnoreCase)
            && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"A chave-mestra de cifra de desenvolvimento (key id '{envelopeKeyId}') está configurada, "
                + $"mas o ambiente é '{environment.EnvironmentName}'. "
                + "Configure a chave do provedor de KMS real ou execute o host em Development.");
        }

        // Same containment rule for the committed attestation signing key:
        // evidence signed by a key anyone with repository access holds proves
        // nothing, so outside Development the host refuses to start with it.
        var attestationKeyId = configuration[$"{AttestationSignerOptions.SectionName}:KeyId"];
        if (attestationKeyId is not null
            && attestationKeyId.Contains(
                AttestationSignerOptions.DevelopmentKeyIdMarker, StringComparison.OrdinalIgnoreCase)
            && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"A chave de assinatura de atestado de desenvolvimento (key id '{attestationKeyId}') está "
                + $"configurada, mas o ambiente é '{environment.EnvironmentName}'. "
                + "Configure a chave do provedor de KMS real ou execute o host em Development.");
        }

        await host.RunAsync();
    }
}
