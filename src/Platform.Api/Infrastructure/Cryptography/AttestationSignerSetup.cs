using Amazon;
using Amazon.KeyManagementService;
using Amazon.Runtime;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Infrastructure.Cryptography;

/// <summary>
/// Composition surface of the attestation signer: binds the options and
/// registers the implementation the configured provider selects, behind the
/// provider-agnostic contract. Switching provider is a configuration change;
/// no caller and no already-signed artifact is affected.
/// </summary>
internal static class AttestationSignerSetup
{
    internal static IServiceCollection AddAttestationSigning(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AttestationSignerOptions>()
            .Bind(configuration.GetSection(AttestationSignerOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => options.HasKnownProvider(),
                "O provedor de assinatura de atestado deve ser 'local' ou 'kms'.")
            .Validate(
                options => options.HasProviderMaterial(),
                "O provedor local de assinatura exige a chave privada PKCS#8 em base64.")
            .ValidateOnStart();

        services.AddSingleton<IAttestationSigner>(serviceProvider =>
        {
            IOptions<AttestationSignerOptions> options =
                serviceProvider.GetRequiredService<IOptions<AttestationSignerOptions>>();
            return string.Equals(
                options.Value.Provider, AttestationSignerOptions.KmsProvider, StringComparison.Ordinal)
                    ? new KmsAttestationSigner(
                        serviceProvider.GetRequiredService<IAmazonKeyManagementService>(), options)
                    : new LocalKeyAttestationSigner(options);
        });

        services.AddSingleton<IAmazonKeyManagementService>(serviceProvider =>
            CreateKmsClient(serviceProvider.GetRequiredService<IOptions<AttestationSignerOptions>>().Value));

        return services;
    }

    /// <summary>
    /// Without static keys the SDK falls back to its default credential chain
    /// (instance profile, environment), which is the production path; tests
    /// and local runs point the service URL at the emulator with static keys.
    /// </summary>
    private static AmazonKeyManagementServiceClient CreateKmsClient(AttestationSignerOptions options)
    {
        var config = new AmazonKeyManagementServiceConfig();
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
            ? new AmazonKeyManagementServiceClient(
                new BasicAWSCredentials(options.AccessKey, options.SecretKey), config)
            : new AmazonKeyManagementServiceClient(config);
    }
}
