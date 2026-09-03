namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Capacity;

/// <summary>
/// Binds the capacity section and refuses, at startup, a shape the module could
/// not honour later. Three of the guards say the same thing about three values,
/// that an unset ceiling is not a ceiling of zero, and the fourth is the only
/// relation between them the module owns.
/// </summary>
internal static class AttachmentCapacitySetup
{
    internal static IServiceCollection AddAttachmentCapacity(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AttachmentCapacityOptions>()
            .Bind(configuration.GetSection(AttachmentCapacityOptions.SectionName))
            .Validate(
                options => options.MaxAttachmentBytes > 0,
                "O teto por anexo tem de ser declarado e maior que zero; a seção de "
                    + "capacidade é obrigatória, e a ausência dela derruba a partida em vez "
                    + "de recusar em silêncio todo anexo registrado.")
            .Validate(
                options => options.MaxEnvelopeBytes > 0,
                "O envelope somado por notificação tem de ser declarado e maior que zero; "
                    + "um envelope ausente recusaria toda notificação com anexo em vez de "
                    + "recusar o anexo.")
            .Validate(
                options => options.MaxAttachmentsPerNotification > 0,
                "A quantidade máxima de anexos por notificação tem de ser declarada e maior "
                    + "que zero; uma quantidade ausente não deixaria nenhum conjunto ser "
                    + "aceito.")
            .Validate(
                // Derived from what the two values mean, not chosen here. A
                // per attachment ceiling above the sum admits, at
                // registration, one attachment that no notification could ever
                // carry, and the producer only finds out after spending the
                // transfer.
                options => options.MaxAttachmentBytes <= options.MaxEnvelopeBytes,
                "O teto por anexo não pode ultrapassar o envelope somado; um anexo aceito no "
                    + "registro que o envelope nunca carregaria só seria recusado depois de o "
                    + "produtor gastar a transferência.")
            .ValidateOnStart();

        return services;
    }
}
