namespace NotificationHub.Api.Modules.Compliance.Infrastructure.Http;

/// <summary>
/// Published numbers and notices of the audit surface. They are contract, not
/// configuration: an auditor reads the declared window to know what the answer
/// covers, and moving one silently would change the meaning of an answer that
/// looks unchanged.
/// </summary>
internal static class AuditReadContract
{
    /// <summary>
    /// How far back from the notification's acceptance the evidence window
    /// reaches. The trail is partitioned by occurrence month and its indexes are
    /// local per partition, so a read without a window would scan every month;
    /// the span covers the recipient's consent and registration history around
    /// the notification, which is the part of the ledger that explains it.
    /// </summary>
    internal static readonly TimeSpan EvidenceLookback = TimeSpan.FromDays(180);

    /// <summary>
    /// Notice the OpenAPI description carries on the provider fields, in the
    /// exact words the answer must be read with.
    /// </summary>
    internal const string ProviderAcceptanceNotice =
        "Os campos sentAt e providerMessageId afirmam que o provedor aceitou a mensagem, nunca que ela foi "
        + "entregue ao cliente. Confirmação de entrega depende de eventos do provedor, que a fase atual não "
        + "coleta e por isso não declara em nenhum membro da resposta.";

    /// <summary>Notice the OpenAPI description carries on the disclosure record of every call.</summary>
    internal const string DisclosureNotice =
        "Toda chamada bem-sucedida grava audit.read na trilha antes de emitir qualquer byte do corpo. "
        + "Falha ao gravar derruba a resposta e nada é divulgado.";

    /// <summary>Notice the OpenAPI description carries on the accesses the answer lists.</summary>
    internal const string PriorAccessNotice =
        "A lista de acessos traz apenas os acessos anteriores à chamada atual, com o corte declarado em "
        + "disclosure.composedAt. O audit.read da chamada atual não aparece nela.";

    /// <summary>
    /// Notice the OpenAPI description carries on where the reason of a device
    /// invalidation lives, stated in words so a reader does not conclude from
    /// the omission that the answer forgot it.
    /// </summary>
    internal const string DeviceInvalidationReasonNotice =
        "O bloco de estado declara que um registro de dispositivo foi invalidado e quando, nunca por quê. "
        + "O motivo dado pelo provedor é fato de trilha e viaja no bloco de trilha desta mesma resposta, "
        + "no elo do próprio registro. Ele não voltará para o bloco de estado: uma coluna de motivo criaria "
        + "segunda morada para a mesma verdade.";

    /// <summary>Notice the OpenAPI description carries on the disclosed content form.</summary>
    internal const string ContentFormNotice =
        "A rota serve a forma mascarada do conteúdo e recomputa contentHashMasked com o hasher canônico do "
        + "catálogo. A verificação criptográfica da forma completa não é possível depois do mascaramento: "
        + "contentHashFull sai declarado, sem verificação, para confronto com evidência externa.";
}
