using Pulse.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class TaskProgressCreditConfiguration : IEntityTypeConfiguration<TaskProgressCredit>
{
    public void Configure(EntityTypeBuilder<TaskProgressCredit> builder)
    {
        builder.ToTable("task_progress_credits");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id");
        builder.Property(c => c.TaskId).HasColumnName("task_id").IsRequired();
        builder.Property(c => c.EngineerId).HasColumnName("engineer_id");
        builder.Property(c => c.Points).HasColumnName("points").HasPrecision(10, 3).IsRequired();
        builder.Property(c => c.ProgressAfter).HasColumnName("progress_after").IsRequired();
        builder.Property(c => c.CreditedAt).HasColumnName("credited_at").IsRequired();
        builder.Property(c => c.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(c => new { c.EngineerId, c.CreditedAt }).HasDatabaseName("ix_task_progress_credits_engineer_id_credited_at");
        builder.HasIndex(c => c.TaskId).HasDatabaseName("ix_task_progress_credits_task_id");
    }
}
