using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence.Configurations;

internal sealed class ProducerApplicationGrantConfiguration
    : IEntityTypeConfiguration<ProducerApplicationGrant>
{
    public void Configure(EntityTypeBuilder<ProducerApplicationGrant> builder)
    {
        builder.ToTable("producer_application_grant");

        builder.Property(grant => grant.Issuer)
            .HasColumnName("issuer")
            .HasMaxLength(ProducerApplicationGrant.MaxIssuerLength);
        builder.Property(grant => grant.ClaimKind)
            .HasColumnName("claim_kind")
            .HasMaxLength(ProducerApplicationGrant.MaxClaimKindLength);
        builder.Property(grant => grant.PrincipalId)
            .HasColumnName("principal_id")
            .HasMaxLength(ProducerApplicationGrant.MaxPrincipalIdLength);
        builder.Property(grant => grant.Application)
            .HasColumnName("application")
            .HasMaxLength(Attachment.MaxApplicationLength);

        builder.HasKey(
            grant => new
            {
                grant.Issuer,
                grant.ClaimKind,
                grant.PrincipalId,
                grant.Application,
            });
    }
}
