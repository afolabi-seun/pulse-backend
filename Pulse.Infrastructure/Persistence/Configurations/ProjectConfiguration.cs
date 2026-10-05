using Pulse.Domain.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("projects");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id");
        builder.Property(p => p.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(p => p.Code).HasColumnName("code").HasMaxLength(10).IsRequired();
        builder.Property(p => p.Description).HasColumnName("description");
        builder.Property(p => p.Status).HasColumnName("status")
            .HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.Property(p => p.OwnerTeamId).HasColumnName("owner_team_id");
        OrganizationConfiguration.ConfigureOrganizationId(builder, "projects", index: false);
        builder.Property(p => p.PersonalOwnerId).HasColumnName("personal_owner_id");
        // At most one personal project per person.
        builder.HasIndex(p => p.PersonalOwnerId).IsUnique()
            .HasFilter("personal_owner_id is not null").HasDatabaseName("ux_projects_personal_owner_id");
        builder.HasIndex(p => p.Status).HasDatabaseName("ix_projects_status");
        // Unique per organization, not app-wide — two orgs can each have an "ENG" project.
        builder.HasIndex(p => new { p.OrganizationId, p.Code }).IsUnique().HasDatabaseName("ux_projects_organization_id_code");
    }
}
