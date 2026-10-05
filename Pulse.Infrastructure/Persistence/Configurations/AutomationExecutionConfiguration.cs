using Pulse.Domain.Automations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class AutomationExecutionConfiguration : IEntityTypeConfiguration<AutomationExecution>
{
    public void Configure(EntityTypeBuilder<AutomationExecution> builder)
    {
        builder.ToTable("automation_executions");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.AutomationRuleId).HasColumnName("automation_rule_id").IsRequired();
        builder.Property(e => e.TaskId).HasColumnName("task_id").IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        // The lookup AutomationRuleScanner does for every blocked-task candidate: "did this rule
        // already act on this task, and how recently?"
        builder.HasIndex(e => new { e.AutomationRuleId, e.TaskId }).HasDatabaseName("ix_automation_executions_rule_task");
    }
}
