using Pulse.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class TaskEstimationSessionConfiguration : IEntityTypeConfiguration<TaskEstimationSession>
{
    public void Configure(EntityTypeBuilder<TaskEstimationSession> builder)
    {
        builder.ToTable("task_estimation_sessions");
        builder.HasKey(s => s.TaskId);
        builder.Property(s => s.TaskId).HasColumnName("task_id");
        builder.Property(s => s.IsRevealed).HasColumnName("is_revealed").IsRequired();
        builder.Property(s => s.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(s => s.PendingApprovalPoints).HasColumnName("pending_approval_points");
        builder.Property(s => s.SubmittedBy).HasColumnName("submitted_by");
        builder.Property(s => s.SubmittedAt).HasColumnName("submitted_at");
        builder.Property(s => s.EscalatedToHead).HasColumnName("escalated_to_head").IsRequired().HasDefaultValue(false);
    }
}
