using Pulse.Domain.Automations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class AutomationRuleConfiguration : IEntityTypeConfiguration<AutomationRule>
{
    public void Configure(EntityTypeBuilder<AutomationRule> builder)
    {
        builder.ToTable("automation_rules");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.OwnerEngineerId).HasColumnName("owner_engineer_id").IsRequired();
        builder.Property(r => r.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(r => r.TeamId).HasColumnName("team_id").IsRequired();
        builder.Property(r => r.ThresholdDays).HasColumnName("threshold_days").IsRequired();
        builder.Property(r => r.IsActive).HasColumnName("is_active").IsRequired().HasDefaultValue(true);
        builder.Property(r => r.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(r => r.OwnerEngineerId).HasDatabaseName("ix_automation_rules_owner_engineer_id");
        // The scanner's own read path.
        builder.HasIndex(r => r.IsActive).HasDatabaseName("ix_automation_rules_active");
    }
}
