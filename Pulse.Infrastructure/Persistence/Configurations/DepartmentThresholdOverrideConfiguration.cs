using Pulse.Application.Overwork;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class DepartmentThresholdOverrideConfiguration : IEntityTypeConfiguration<DepartmentThresholdOverride>
{
    public void Configure(EntityTypeBuilder<DepartmentThresholdOverride> builder)
    {
        builder.ToTable("department_overwork_thresholds");
        builder.HasKey(d => new { d.OrganizationId, d.Department });
        OrganizationConfiguration.ConfigureOrganizationId(builder, "department_overwork_thresholds", index: false);
        builder.Property(d => d.Department).HasColumnName("department").HasMaxLength(100).IsRequired();
        builder.Property(d => d.LoadVsBaselineRatio).HasColumnName("load_vs_baseline_ratio");
        builder.Property(d => d.MaxConcurrentTasks).HasColumnName("max_concurrent_tasks");
        builder.Property(d => d.StaleCycleMultiplier).HasColumnName("stale_cycle_multiplier");
        builder.Property(d => d.SignalsRequiredToFlag).HasColumnName("signals_required_to_flag");
        builder.Property(d => d.UpdatedBy).HasColumnName("updated_by");
        builder.Property(d => d.UpdatedAt).HasColumnName("updated_at");
    }
}
