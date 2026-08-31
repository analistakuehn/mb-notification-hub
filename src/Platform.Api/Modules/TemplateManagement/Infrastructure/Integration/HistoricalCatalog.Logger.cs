namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;

/// <summary>
/// The witness of the historical read. Every event here describes state no
/// legitimate path produces, and all three are recorded at error level. The
/// consuming compliance surface turns a withheld version into a missing
/// template block, which says nothing on its own about the row existing. The
/// answer does carry the layout pin the version declared, so a withheld layout
/// is no longer read as a message that was framed by nothing; what the answer
/// still cannot say is which of the two withholdings happened, and these two
/// events are the only place that difference is written down.
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
        Message = "O layout {LayoutKey}, versão {LayoutVersion}, fixado pela versão {Version} do template {TemplateKey}, tem status '{VersionStatus}', nunca foi publicado e ficou fora da resposta histórica; a resposta declara o pino e omite o layout, sem o hash que a aprovação assinou.")]
    internal static partial void PinnedLayoutVersionWithheld(
        this ILogger logger,
        string layoutKey,
        int layoutVersion,
        string templateKey,
        int version,
        string versionStatus);

    [LoggerMessage(
        EventId = 3101,
        Level = LogLevel.Error,
        Message = "O layout {LayoutKey}, versão {LayoutVersion}, fixado pela versão {Version} do template {TemplateKey}, não foi encontrado e ficou fora da resposta histórica; a resposta declara o pino e omite o layout, sem o hash que a aprovação assinou.")]
    internal static partial void PinnedLayoutVersionMissing(
        this ILogger logger,
        string layoutKey,
        int layoutVersion,
        string templateKey,
        int version);
}
