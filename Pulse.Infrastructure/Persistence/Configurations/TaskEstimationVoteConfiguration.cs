using Pulse.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class TaskEstimationVoteConfiguration : IEntityTypeConfiguration<TaskEstimationVote>
{
    public void Configure(EntityTypeBuilder<TaskEstimationVote> builder)
    {
        builder.ToTable("task_estimation_votes");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).HasColumnName("id");
        builder.Property(v => v.TaskId).HasColumnName("task_id").IsRequired();
        builder.Property(v => v.VoterId).HasColumnName("voter_id").IsRequired();
        builder.Property(v => v.Points).HasColumnName("points").IsRequired();
        builder.Property(v => v.SubmittedAt).HasColumnName("submitted_at").IsRequired();

        builder.HasIndex(v => v.TaskId).HasDatabaseName("ix_task_estimation_votes_task_id");
        builder.HasIndex(v => new { v.TaskId, v.VoterId })
            .IsUnique().HasDatabaseName("ix_task_estimation_votes_task_voter");
    }
}
