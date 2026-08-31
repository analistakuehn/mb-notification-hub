namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;

/// <summary>
/// The witness of the historical read. Both events describe state no
/// legitimate path produces, and both are recorded at error level because the
/// harm they carry is silence: the consuming compliance surface turns a
/// withheld version into a missing template block and a withheld layout into
/// an absent pin, and neither omission says on its own that the row exists.
/// </summary>
internal static partial class HistoricalCatalogLogger
{
    [LoggerMessage(
        EventId = 2120,
        Level = LogLevel.Error,
        Message = "A versão {Version} do template {TemplateKey} da aplicação {Application} tem status '{VersionStatus}', nunca foi publicada e ficou fora da resposta histórica; quem a nomeou recebe não encontrado, indistinguível de uma versão que nunca existiu.")]
    internal static partial void TemplateVersionWithheld(
        this ILogger logger,
        string application,
        string templateKey,
        int version,
        string versionStatus);

    [LoggerMessage(
        EventId = 3100,
        Level = LogLevel.Error,
        Message = "O layout {LayoutKey}, versão {LayoutVersion}, fixado pela versão {Version} do template {TemplateKey}, tem status '{VersionStatus}', nunca foi publicado e ficou fora da resposta histórica; a resposta apenas omite o layout, como omite um pino que não resolve mais.")]
    internal static partial void PinnedLayoutVersionWithheld(
        this ILogger logger,
        string layoutKey,
        int layoutVersion,
        string templateKey,
        int version,
        string versionStatus);
}
