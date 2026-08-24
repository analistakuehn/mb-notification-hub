using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;

internal sealed class PostgresKillSwitchSnapshotSource(IServiceScopeFactory scopeFactory)
    : IKillSwitchSnapshotSource
{
    public async Task<IReadOnlySet<KillSwitchAddress>> LoadActiveAsync(
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var rows = await db.KillSwitches
            .AsNoTracking()
            .Where(entry => entry.State == KillSwitchStates.Active)
            .Select(entry => new { entry.Scope, entry.Key })
            .ToListAsync(cancellationToken);

        var active = new HashSet<KillSwitchAddress>();
        foreach (var row in rows)
        {
            if (!KillSwitchScopes.TryParse(row.Scope, out KillSwitchScope parsed))
            {
                throw new InvalidOperationException(
                    $"O estado persistido contém o escopo de kill switch desconhecido '{row.Scope}'.");
            }

            active.Add(new KillSwitchAddress(parsed, row.Key));
        }

        return active;
    }
}
