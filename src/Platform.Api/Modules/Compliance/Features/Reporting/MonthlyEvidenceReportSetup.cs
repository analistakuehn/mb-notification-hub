using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NotificationHub.Api.Modules.Compliance.Features.Reporting;

/// <summary>
/// Composition of the monthly evidence report: the options, the use case and
/// its scheduler. It composes nothing of the modules it reads and nothing of
/// the module it archives through, because this module owns no data and no
/// store: the host that hosts this job is what wires the published surfaces
/// behind the three contracts the handler asks for.
/// </summary>
internal static class MonthlyEvidenceReportSetup
{
    internal static IServiceCollection AddComplianceMonthlyEvidenceReport(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<MonthlyEvidenceReportOptions>()
            .Bind(configuration.GetSection(MonthlyEvidenceReportOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => options.Interval >= TimeSpan.FromMinutes(1),
                "A cadência do relatório mensal de evidências deve ser de pelo menos um minuto.")
            .Validate(
                options => options.Interval <= TimeSpan.FromDays(7),
                "A cadência do relatório mensal de evidências deve ser de no máximo sete dias; acima disso um mês fechado demora demais a aparecer.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ComposeMonthlyEvidence.Handler>();
        services.AddHostedService<MonthlyEvidenceReportService>();
        return services;
    }
}
