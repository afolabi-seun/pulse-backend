using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class ThresholdSettingConfiguration : IEntityTypeConfiguration<ThresholdSetting>
{
    public void Configure(EntityTypeBuilder<ThresholdSetting> builder)
    {
        builder.ToTable("threshold_settings");
        builder.HasKey(t => new { t.OrganizationId, t.Key });
        OrganizationConfiguration.ConfigureOrganizationId(builder, "threshold_settings", index: false);
        builder.Property(t => t.Key).HasColumnName("key").HasMaxLength(100).IsRequired();
        builder.Property(t => t.Value).HasColumnName("value").HasColumnType("text").IsRequired();
    }
}
