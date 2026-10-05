using Pulse.Domain.Alerts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class AlertMetricSnapshotConfiguration : IEntityTypeConfiguration<AlertMetricSnapshot>
{
    public void Configure(EntityTypeBuilder<AlertMetricSnapshot> builder)
    {
        builder.ToTable("alert_metric_snapshots");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id");
        builder.Property(s => s.Metric).HasColumnName("metric")
            .HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(s => s.ScopeType).HasColumnName("scope_type")
            .HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.ScopeId).HasColumnName("scope_id").IsRequired();
        builder.Property(s => s.Value).HasColumnName("value").IsRequired();
        builder.Property(s => s.CreatedAt).HasColumnName("created_at").IsRequired();

        // The "find the closest snapshot before this cutoff" read GetChangeAsync does on every
        // percent-change computation.
        builder.HasIndex(s => new { s.Metric, s.ScopeType, s.ScopeId, s.CreatedAt })
            .HasDatabaseName("ix_alert_metric_snapshots_lookup");
    }
}
