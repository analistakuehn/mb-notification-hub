using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NotificationHub.Api.Infrastructure.Partitioning;

/// <summary>
/// Composition surface of the partitioning infrastructure: each module
/// registers its coverage check naming its own DbContext, schema, and table,
/// so the platform never references a module type.
/// </summary>
internal static class PartitioningSetup
{
    /// <summary>
    /// Registers a monthly partition-coverage check over the database of
    /// <typeparamref name="TDbContext"/>. The minimum-days accessor runs at
    /// every check execution against the scoped service provider, so the
    /// module can read it from its bound options.
    /// </summary>
    internal static IHealthChecksBuilder AddMonthlyPartitionCoverageCheck<TDbContext>(
        this IHealthChecksBuilder builder,
        string name,
        string schema,
        string table,
        Func<IServiceProvider, int> minimumFutureDays)
        where TDbContext : DbContext
        => builder.Add(new HealthCheckRegistration(
            name,
            serviceProvider => new PartitionCoverageHealthCheck(
                serviceProvider.GetRequiredService<TDbContext>().Database,
                schema,
                table,
                minimumFutureDays(serviceProvider),
                serviceProvider.GetRequiredService<TimeProvider>()),
            failureStatus: null,
            tags: null));
}
