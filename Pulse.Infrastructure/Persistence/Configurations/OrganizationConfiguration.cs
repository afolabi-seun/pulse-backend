using Pulse.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("organizations");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).HasColumnName("id");
        builder.Property(o => o.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(o => o.Slug).HasColumnName("slug").HasMaxLength(50).IsRequired();
        builder.Property(o => o.BillingEmail).HasColumnName("billing_email").HasMaxLength(256);
        builder.Property(o => o.IsActive).HasColumnName("is_active").IsRequired();
        builder.Property(o => o.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(o => o.Slug).IsUnique().HasDatabaseName("ux_organizations_slug");
    }

    /// <summary>
    /// Shared mapping for the OrganizationId column on the tenancy-root tables (teams, engineers,
    /// projects). The column default is the default org, so inserts that don't set it — every insert,
    /// until Phase 2 makes the caller's org flow through — still land in a real organization, and
    /// Phase 1 can tighten the column to NOT NULL without a second backfill.
    /// </summary>
    internal static void ConfigureOrganizationId<T>(EntityTypeBuilder<T> builder, string table)
        where T : class
    {
        builder.Property<Guid?>("OrganizationId")
            .HasColumnName("organization_id")
            .HasDefaultValue(Organization.DefaultId);

        builder.HasIndex("OrganizationId").HasDatabaseName($"ix_{table}_organization_id");

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey("OrganizationId")
            .HasConstraintName($"fk_{table}_organizations_organization_id")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
