using Pulse.Domain.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class ProjectMemberConfiguration : IEntityTypeConfiguration<ProjectMember>
{
    public void Configure(EntityTypeBuilder<ProjectMember> builder)
    {
        builder.ToTable("project_members");
        builder.HasKey(m => new { m.ProjectId, m.EngineerId });
        builder.Property(m => m.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(m => m.EngineerId).HasColumnName("engineer_id").IsRequired();
        builder.Property(m => m.AddedAt).HasColumnName("added_at").IsRequired();

        builder.HasIndex(m => m.ProjectId)
            .HasDatabaseName("ix_project_members_project_id");

        builder.HasIndex(m => m.EngineerId)
            .HasDatabaseName("ix_project_members_engineer_id");
    }
}
