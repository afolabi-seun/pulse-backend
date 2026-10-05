using Pulse.Domain.Feedback;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class FeedbackConfiguration : IEntityTypeConfiguration<Feedback>
{
    public void Configure(EntityTypeBuilder<Feedback> builder)
    {
        builder.ToTable("feedback");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).HasColumnName("id");
        builder.Property(f => f.EngineerId).HasColumnName("engineer_id").IsRequired();
        builder.Property(f => f.Text).HasColumnName("text").IsRequired();
        builder.Property(f => f.WeekOf).HasColumnName("week_of").IsRequired();
        builder.Property(f => f.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(f => f.TaskId).HasColumnName("task_id");
        builder.Property(f => f.ReplyText).HasColumnName("reply_text").HasMaxLength(5000);
        builder.Property(f => f.RepliedBy).HasColumnName("replied_by");
        builder.Property(f => f.RepliedAt).HasColumnName("replied_at");

        builder.HasIndex(f => f.WeekOf).HasDatabaseName("ix_feedback_week_of");
    }
}
