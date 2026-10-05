using Pulse.Domain.Alerts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class GoogleChatThreadConfiguration : IEntityTypeConfiguration<GoogleChatThread>
{
    public void Configure(EntityTypeBuilder<GoogleChatThread> builder)
    {
        builder.ToTable("google_chat_threads");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id");
        builder.Property(t => t.AlertRuleId).HasColumnName("alert_rule_id").IsRequired();
        builder.Property(t => t.SpaceId).HasColumnName("space_id").HasMaxLength(200).IsRequired();
        builder.Property(t => t.ThreadName).HasColumnName("thread_name").HasMaxLength(200).IsRequired();
        builder.Property(t => t.CreatedAt).HasColumnName("created_at").IsRequired();

        // The lookup HandleGoogleChatReplyJob does on every inbound message: "is this thread one we started?"
        builder.HasIndex(t => new { t.SpaceId, t.ThreadName }).IsUnique().HasDatabaseName("ux_google_chat_threads_space_thread");
    }
}
