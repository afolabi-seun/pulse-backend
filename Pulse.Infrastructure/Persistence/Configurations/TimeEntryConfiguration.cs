using Pulse.Domain.TimeEntries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class TimeEntryConfiguration : IEntityTypeConfiguration<TimeEntry>
{
    public void Configure(EntityTypeBuilder<TimeEntry> builder)
    {
        builder.ToTable("time_entries");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id");
        builder.Property(t => t.EngineerId).HasColumnName("engineer_id").IsRequired();
        builder.Property(t => t.Date).HasColumnName("date").IsRequired();
        builder.Property(t => t.Category).HasColumnName("category").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(t => t.TaskId).HasColumnName("task_id");
        builder.Property(t => t.SubtaskId).HasColumnName("subtask_id");
        builder.Property(t => t.ProjectId).HasColumnName("project_id");
        builder.Property(t => t.Hours).HasColumnName("hours").HasPrecision(4, 2).IsRequired();
        builder.Property(t => t.Note).HasColumnName("note").HasMaxLength(1000);
        builder.Property(t => t.CreatedAt).HasColumnName("logged_at").IsRequired();

        // Not unique — a timesheet is many line items per day (two meetings, a task worked in
        // two sessions), unlike CheckIn's one-per-day upsert. Index only for aggregation queries.
        builder.HasIndex(t => new { t.EngineerId, t.Date }).HasDatabaseName("ix_time_entries_engineer_id_date");
        builder.HasIndex(t => t.TaskId).HasDatabaseName("ix_time_entries_task_id");
    }
}
