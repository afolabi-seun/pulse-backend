using Pulse.Domain.Alerts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class AlertRuleConfiguration : IEntityTypeConfiguration<AlertRule>
{
    public void Configure(EntityTypeBuilder<AlertRule> builder)
    {
        builder.ToTable("alert_rules");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.OwnerEngineerId).HasColumnName("owner_engineer_id").IsRequired();
        builder.Property(r => r.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(r => r.Metric).HasColumnName("metric")
            .HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(r => r.ScopeType).HasColumnName("scope_type")
            .HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(r => r.ScopeId).HasColumnName("scope_id").IsRequired();
        builder.Property(r => r.Comparator).HasColumnName("comparator")
            .HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(r => r.Threshold).HasColumnName("threshold").IsRequired();
        builder.Property(r => r.DeliverInApp).HasColumnName("deliver_in_app").IsRequired();
        builder.Property(r => r.DeliverEmail).HasColumnName("deliver_email").IsRequired();
        builder.Property(r => r.DeliverWebhook).HasColumnName("deliver_webhook").IsRequired().HasDefaultValue(false);
        builder.Property(r => r.WebhookUrl).HasColumnName("webhook_url").HasMaxLength(500);
        builder.Property(r => r.SlackChannel).HasColumnName("slack_channel").HasMaxLength(100);
        builder.Property(r => r.GoogleChatSpaceId).HasColumnName("google_chat_space_id").HasMaxLength(200);
        builder.Property(r => r.IsActive).HasColumnName("is_active").IsRequired().HasDefaultValue(true);
        builder.Property(r => r.LastTriggeredAt).HasColumnName("last_triggered_at");
        builder.Property(r => r.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(r => r.OwnerEngineerId).HasDatabaseName("ix_alert_rules_owner_engineer_id");
        // The scanner's own read path — active rules grouped by what they watch.
        builder.HasIndex(r => new { r.IsActive, r.Metric, r.ScopeType, r.ScopeId }).HasDatabaseName("ix_alert_rules_active_metric_scope");
    }
}
