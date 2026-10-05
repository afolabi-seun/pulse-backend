using Pulse.Domain.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class ProjectFollowConfiguration : IEntityTypeConfiguration<ProjectFollow>
{
    public void Configure(EntityTypeBuilder<ProjectFollow> builder)
    {
        builder.ToTable("project_follows");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).HasColumnName("id");
        builder.Property(f => f.FollowerId).HasColumnName("follower_id").IsRequired();
        builder.Property(f => f.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(f => f.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(f => new { f.FollowerId, f.ProjectId })
            .IsUnique()
            .HasDatabaseName("ix_project_follows_follower_project");

        builder.HasIndex(f => f.ProjectId)
            .HasDatabaseName("ix_project_follows_project_id");
    }
}
