using Pulse.Domain.Epics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class EpicConfiguration : IEntityTypeConfiguration<Epic>
{
    public void Configure(EntityTypeBuilder<Epic> builder)
    {
        builder.ToTable("epics");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.Title).HasColumnName("title").HasMaxLength(500).IsRequired();
        builder.Property(e => e.Description).HasColumnName("description");
        builder.Property(e => e.AcceptanceCriteria).HasColumnName("acceptance_criteria");
        builder.Property(e => e.Status).HasColumnName("status")
            .HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(e => e.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(e => e.SprintId).HasColumnName("sprint_id");
        builder.Property(e => e.Order).HasColumnName("order").IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(e => e.ProjectId).HasDatabaseName("ix_epics_project_id");
        builder.HasIndex(e => e.SprintId).HasDatabaseName("ix_epics_sprint_id");
    }
}
