using Pulse.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class TaskDependencyConfiguration : IEntityTypeConfiguration<TaskDependency>
{
    public void Configure(EntityTypeBuilder<TaskDependency> builder)
    {
        builder.ToTable("task_dependencies");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).HasColumnName("id");
        builder.Property(d => d.BlockingTaskId).HasColumnName("blocking_task_id").IsRequired();
        builder.Property(d => d.DependentTaskId).HasColumnName("dependent_task_id").IsRequired();
        builder.Property(d => d.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(d => d.BlockingTaskId).HasDatabaseName("ix_task_dependencies_blocking_task_id");
        builder.HasIndex(d => d.DependentTaskId).HasDatabaseName("ix_task_dependencies_dependent_task_id");
        builder.HasIndex(d => new { d.BlockingTaskId, d.DependentTaskId })
            .IsUnique().HasDatabaseName("ix_task_dependencies_unique_pair");
    }
}
