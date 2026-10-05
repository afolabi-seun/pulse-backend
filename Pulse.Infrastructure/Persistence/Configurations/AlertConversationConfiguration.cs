using Pulse.Domain.Alerts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class AlertConversationConfiguration : IEntityTypeConfiguration<AlertConversation>
{
    public void Configure(EntityTypeBuilder<AlertConversation> builder)
    {
        builder.ToTable("alert_conversations");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id");
        builder.Property(c => c.AlertRuleId).HasColumnName("alert_rule_id").IsRequired();
        builder.Property(c => c.ChannelId).HasColumnName("channel_id").HasMaxLength(50).IsRequired();
        builder.Property(c => c.ThreadTs).HasColumnName("thread_ts").HasMaxLength(50).IsRequired();
        builder.Property(c => c.CreatedAt).HasColumnName("created_at").IsRequired();

        // The lookup a Slack event handler does on every inbound reply: "is this thread one we started?"
        builder.HasIndex(c => new { c.ChannelId, c.ThreadTs }).IsUnique().HasDatabaseName("ux_alert_conversations_channel_thread");
    }
}
