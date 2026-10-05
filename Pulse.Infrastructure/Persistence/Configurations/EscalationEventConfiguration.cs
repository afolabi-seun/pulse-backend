using Pulse.Domain.Escalations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class EscalationEventConfiguration : IEntityTypeConfiguration<EscalationEvent>
{
    public void Configure(EntityTypeBuilder<EscalationEvent> builder)
    {
        builder.ToTable("escalation_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.TaskId).HasColumnName("task_id").IsRequired();
        builder.Property(e => e.Level).HasColumnName("level")
            .HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(e => e.FiredAt).HasColumnName("fired_at").IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        // Idempotency check — the scanner queries (task_id, level) before firing
        builder.HasIndex(e => new { e.TaskId, e.Level }).HasDatabaseName("ix_escalation_events_task_id_level");
    }
}
