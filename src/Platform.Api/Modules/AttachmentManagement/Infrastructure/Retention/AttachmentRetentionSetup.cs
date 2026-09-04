using NotificationHub.Api.Modules.AttachmentManagement.Domain;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Retention;

/// <summary>
/// Registers the sweep of abandoned attachments and the values that bound it.
/// The sweep is registered wherever the module is composed and the scheduler
/// that drives it is not: a job that removes durable bytes is not work that
/// may run once per replica of a request-serving host.
/// </summary>
internal static class AttachmentRetentionSetup
{
    internal static IServiceCollection AddAttachmentRetention(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AttachmentRetentionOptions>()
            .Bind(configuration.GetSection(AttachmentRetentionOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => Windows(options).All(window => window > TimeSpan.Zero),
                "Todos os prazos de retenção têm de ser declarados e maiores que zero em "
                    + "UnstartedUpload, UnvalidatedContent, RefusedContent e "
                    + "WithdrawnRelease; um prazo ausente significaria descartar o conteúdo "
                    + "no instante em que o anexo alcança o estado, que é decisão de produto "
                    + "tomada por omissão.")
            .Validate(
                // Derived from what the delivery side takes to resolve an
                // attempt nobody reported, and not chosen here. A window below
                // that horizon would let this sweep reach an attachment while
                // the system still had something to say about it.
                options => Windows(options).All(
                    window => window >= AttachmentRetentionOptions.UnresolvedAttemptHorizon),
                "Nenhum prazo de retenção pode ser menor que o horizonte de resolução de uma "
                    + "tentativa com resultado desconhecido, de trinta horas; abaixo dele a "
                    + "varredura alcançaria um anexo enquanto a entrega ainda pode reivindicá-lo.")
            .ValidateOnStart();
        services.AddScoped<AttachmentAbandonmentScan>();
        return services;
    }

    private static IEnumerable<TimeSpan> Windows(AttachmentRetentionOptions options)
    {
        AttachmentRetentionWindows windows = options.Windows();
        return
        [
            windows.UnstartedUpload,
            windows.UnvalidatedContent,
            windows.RefusedContent,
            windows.WithdrawnRelease,
        ];
    }
}
