using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.ContactConsent.Domain;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;

/// <summary>
/// Persistence boundary owned exclusively by the ContactConsent bounded
/// context (schema <c>contactconsent</c>). The consent table is append-only
/// by construction: the model maps no mutable member and a row trigger
/// rejects UPDATE and DELETE. None of these tables is partitioned, which is
/// what lets their unique keys exist without the partition column.
/// </summary>
public sealed class ContactConsentDbContext(DbContextOptions<ContactConsentDbContext> options)
    : DbContext(options)
{
    public DbSet<RecipientProfile> RecipientProfiles => Set<RecipientProfile>();

    public DbSet<ContactPoint> ContactPoints => Set<ContactPoint>();

    public DbSet<Consent> Consents => Set<Consent>();

    public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();

    public DbSet<SuppressionSignalRecord> SuppressionSignals => Set<SuppressionSignalRecord>();

    public DbSet<ContactSuppression> Suppressions => Set<ContactSuppression>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("contactconsent");
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(ContactConsentDbContext).Assembly,
            type => type.Namespace?.StartsWith(
                "NotificationHub.Api.Modules.ContactConsent",
                StringComparison.Ordinal) is true);
        base.OnModelCreating(modelBuilder);
    }
}
