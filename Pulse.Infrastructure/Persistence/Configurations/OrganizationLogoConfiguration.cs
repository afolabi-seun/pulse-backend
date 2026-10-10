using Pulse.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class OrganizationLogoConfiguration : IEntityTypeConfiguration<OrganizationLogo>
{
    public void Configure(EntityTypeBuilder<OrganizationLogo> builder)
    {
        builder.ToTable("organization_logos");
        builder.HasKey(l => l.OrganizationId);
        builder.Property(l => l.OrganizationId).HasColumnName("organization_id");
        builder.Property(l => l.ContentType).HasColumnName("content_type").HasMaxLength(32).IsRequired();
        builder.Property(l => l.Data).HasColumnName("data").IsRequired();

        builder.HasOne<Organization>()
            .WithOne()
            .HasForeignKey<OrganizationLogo>(l => l.OrganizationId)
            .HasConstraintName("fk_organization_logos_organizations_organization_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
