namespace NotificationHub.Api.Infrastructure.Cryptography;

/// <summary>
/// Composition surface of the envelope encryption: binds the options and
/// registers the local-key implementation behind the provider-agnostic
/// contract.
/// </summary>
internal static class EnvelopeCipherSetup
{
    internal static IServiceCollection AddEnvelopeEncryption(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<EnvelopeCipherOptions>()
            .Bind(configuration.GetSection(EnvelopeCipherOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IEnvelopeCipher, LocalKeyEnvelopeCipher>();
        return services;
    }
}
