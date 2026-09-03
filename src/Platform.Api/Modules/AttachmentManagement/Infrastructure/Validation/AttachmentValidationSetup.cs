using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Validation;

/// <summary>
/// Registers the policy gate and refuses, at startup, the values it could not
/// honour later. Every guard below is derived from something the module
/// already decided, and none of them is a product limit: the duration an
/// operator wants and the types an operator admits are not this file's to
/// choose.
/// </summary>
internal static class AttachmentValidationSetup
{
    internal static IServiceCollection AddAttachmentValidation(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AttachmentValidationOptions>()
            .Bind(configuration.GetSection(AttachmentValidationOptions.SectionName))
            .Validate(
                options => options.AdmittedContentTypes.All(AttachmentContentSignatures.Knows),
                "A lista de tipos admitidos só aceita tipos que a tabela de assinaturas "
                    + "reconhece; um tipo que nada detecta recusaria todo arquivo desse tipo.")
            .Validate(
                // Derived from the row, not chosen here: the release table
                // refuses an expiry that is not after the release, so a
                // validity of zero or less would fail every grant at the
                // insert, long after the value was accepted.
                options => options.ReleaseValidity > TimeSpan.Zero,
                "A validade da liberação tem de ser maior que zero; a linha de liberação "
                    + "recusa um vencimento que não seja posterior ao instante da liberação.")
            .Validate(
                options => FitsTheDeadlineArithmetic(options.ReleaseValidity),
                "A validade da liberação não cabe na aritmética do vencimento; o prazo "
                    + "resultante ultrapassaria a maior data representável.")
            .Validate(
                // Zero is allowed and closes: a wait that starts already over
                // ends on the next validation. Negative is not, because the
                // deadline would fall before the verdict that started it.
                options => options.InconclusiveWindow >= TimeSpan.Zero,
                "A janela do resultado inconclusivo não pode ser negativa; o prazo cairia "
                    + "antes do veredito que o abriu.")
            .Validate(
                options => FitsTheDeadlineArithmetic(options.InconclusiveWindow),
                "A janela do resultado inconclusivo não cabe na aritmética do prazo; o "
                    + "vencimento resultante ultrapassaria a maior data representável.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        // The policy that ships refuses everything the operator did not admit,
        // and the operator admits nothing by default. A verifier arrives as a
        // different registration behind this same interface, and nothing else
        // in the module moves when it does.
        services.TryAddSingleton<IAttachmentContentPolicy, AdmittedTypeContentPolicy>();
        services.AddScoped<AttachmentValidation>();
        return services;
    }

    /// <summary>
    /// Whether a duration added to an instant taken now stays inside a date the
    /// runtime can hold. The ceiling is read from the type and never written as
    /// a number here, because a number here would be a limit on how long an
    /// operator may keep a release usable, and that is not this file's call.
    /// </summary>
    private static bool FitsTheDeadlineArithmetic(TimeSpan duration)
        => duration <= DateTimeOffset.MaxValue - DateTimeOffset.UtcNow;
}
