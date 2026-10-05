using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class ThresholdSettingConfiguration : IEntityTypeConfiguration<ThresholdSetting>
{
    public void Configure(EntityTypeBuilder<ThresholdSetting> builder)
    {
        builder.ToTable("threshold_settings");
        builder.HasKey(t => t.Key);
        builder.Property(t => t.Key).HasColumnName("key").HasMaxLength(100).IsRequired();
        builder.Property(t => t.Value).HasColumnName("value").HasColumnType("text").IsRequired();
    }
}
