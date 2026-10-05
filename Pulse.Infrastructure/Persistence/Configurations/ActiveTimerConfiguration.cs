using Pulse.Domain.TimeEntries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class ActiveTimerConfiguration : IEntityTypeConfiguration<ActiveTimer>
{
    public void Configure(EntityTypeBuilder<ActiveTimer> builder)
    {
        builder.ToTable("active_timers");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id");
        builder.Property(t => t.EngineerId).HasColumnName("engineer_id").IsRequired();
        builder.Property(t => t.Category).HasColumnName("category").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(t => t.TaskId).HasColumnName("task_id");
        builder.Property(t => t.SubtaskId).HasColumnName("subtask_id");
        builder.Property(t => t.StartedAt).HasColumnName("started_at").IsRequired();
        builder.Property(t => t.CreatedAt).HasColumnName("created_at").IsRequired();

        // At most one running timer per engineer — enforced by StartTimerCommand stopping any
        // existing timer first, but the unique index is the real backstop against a race.
        builder.HasIndex(t => t.EngineerId).IsUnique().HasDatabaseName("ix_active_timers_engineer_id");
    }
}
