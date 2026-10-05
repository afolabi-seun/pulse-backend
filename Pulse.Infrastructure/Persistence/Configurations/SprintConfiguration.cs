using Pulse.Domain.Sprints;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class SprintConfiguration : IEntityTypeConfiguration<Sprint>
{
    public void Configure(EntityTypeBuilder<Sprint> builder)
    {
        builder.ToTable("sprints");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id");
        builder.Property(s => s.TeamId).HasColumnName("team_id").IsRequired();
        builder.Property(s => s.ProjectId).HasColumnName("project_id");
        builder.Property(s => s.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(s => s.Goal).HasColumnName("goal").HasMaxLength(1000);
        builder.Property(s => s.StartDate).HasColumnName("start_date").IsRequired();
        builder.Property(s => s.EndDate).HasColumnName("end_date").IsRequired();
        builder.Property(s => s.Status).HasColumnName("status")
            .HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.CapacityPoints).HasColumnName("capacity_points");
        builder.Property(s => s.ShowAndTellDate).HasColumnName("show_and_tell_date");
        builder.Property(s => s.ShowAndTellNotes).HasColumnName("show_and_tell_notes");
        builder.Property(s => s.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(s => s.TeamId).HasDatabaseName("ix_sprints_team_id");
        builder.HasIndex(s => s.ProjectId).HasDatabaseName("ix_sprints_project_id");
    }
}
