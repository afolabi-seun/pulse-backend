using Pulse.Domain.Alerts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class GoogleChatLinkCodeConfiguration : IEntityTypeConfiguration<GoogleChatLinkCode>
{
    public void Configure(EntityTypeBuilder<GoogleChatLinkCode> builder)
    {
        builder.ToTable("google_chat_link_codes");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id");
        builder.Property(c => c.OrganizationId).HasColumnName("organization_id").IsRequired();
        builder.Property(c => c.CodeHash).HasColumnName("code_hash").HasMaxLength(64).IsRequired();
        builder.Property(c => c.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(c => c.CreatedByEngineerId).HasColumnName("created_by_engineer_id").IsRequired();
        builder.Property(c => c.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(c => c.CodeHash).IsUnique().HasDatabaseName("ux_google_chat_link_codes_code_hash");

        builder.HasOne<Pulse.Domain.Organizations.Organization>()
            .WithMany()
            .HasForeignKey(c => c.OrganizationId)
            .HasConstraintName("fk_google_chat_link_codes_organizations_organization_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
