using Pulse.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class TaskHistoryConfiguration : IEntityTypeConfiguration<TaskHistory>
{
    public void Configure(EntityTypeBuilder<TaskHistory> builder)
    {
        builder.ToTable("task_history");
        builder.HasKey(h => h.Id);
        builder.Property(h => h.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(h => h.TaskId).HasColumnName("task_id").IsRequired();
        builder.Property(h => h.Field).HasColumnName("field").HasMaxLength(50).IsRequired();
        builder.Property(h => h.OldValue).HasColumnName("from_value");
        builder.Property(h => h.NewValue).HasColumnName("to_value");
        builder.Property(h => h.ActorId).HasColumnName("actor_id").IsRequired();
        builder.Property(h => h.ChangedAt).HasColumnName("ts").IsRequired();
        builder.Property(h => h.Context).HasColumnName("context").HasMaxLength(20);
        builder.Property(h => h.CreditedEngineerId).HasColumnName("credited_engineer_id");
        builder.Property(h => h.Reason).HasColumnName("reason").HasMaxLength(500);

        builder.HasIndex(h => h.TaskId).HasDatabaseName("ix_task_history_task_id");
    }
}
