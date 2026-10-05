using Pulse.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class SubtaskConfiguration : IEntityTypeConfiguration<Subtask>
{
    public void Configure(EntityTypeBuilder<Subtask> builder)
    {
        builder.ToTable("subtasks");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id");
        builder.Property(s => s.TaskId).HasColumnName("task_id").IsRequired();
        builder.Property(s => s.Title).HasColumnName("title").IsRequired().HasMaxLength(300);
        builder.Property(s => s.IsDone).HasColumnName("is_done").IsRequired();
        builder.Property(s => s.CreatedBy).HasColumnName("created_by").IsRequired();
        builder.Property(s => s.CompletedBy).HasColumnName("completed_by");
        builder.Property(s => s.CompletedAt).HasColumnName("completed_at");
        builder.Property(s => s.AssigneeId).HasColumnName("assignee_id");
        builder.Property(s => s.LoanedFromEngineerId).HasColumnName("loaned_from_engineer_id");
        builder.Property(s => s.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(s => s.TaskId).HasDatabaseName("ix_subtasks_task_id");
    }
}
