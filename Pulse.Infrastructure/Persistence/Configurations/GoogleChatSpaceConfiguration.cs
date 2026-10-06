using Pulse.Domain.Alerts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class GoogleChatSpaceConfiguration : IEntityTypeConfiguration<GoogleChatSpace>
{
    public void Configure(EntityTypeBuilder<GoogleChatSpace> builder)
    {
        builder.ToTable("google_chat_spaces");
        builder.HasKey(s => s.Id);
        // Nullable, no default: a space stays unlinked (no organization) until a head links it.
        builder.Property(s => s.OrganizationId).HasColumnName("organization_id");
        builder.HasIndex(s => s.OrganizationId).HasDatabaseName("ix_google_chat_spaces_organization_id");
        builder.HasOne<Pulse.Domain.Organizations.Organization>()
            .WithMany()
            .HasForeignKey(s => s.OrganizationId)
            .HasConstraintName("fk_google_chat_spaces_organizations_organization_id")
            .OnDelete(DeleteBehavior.Restrict);
        builder.Property(s => s.Id).HasColumnName("id");
        builder.Property(s => s.SpaceId).HasColumnName("space_id").HasMaxLength(200).IsRequired();
        builder.Property(s => s.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
        builder.Property(s => s.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(s => s.SpaceId).IsUnique().HasDatabaseName("ux_google_chat_spaces_space_id");
    }
}
