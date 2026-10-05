using Pulse.Domain.Vitals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class VitalsResponseConfiguration : IEntityTypeConfiguration<VitalsResponse>
{
    public void Configure(EntityTypeBuilder<VitalsResponse> builder)
    {
        builder.ToTable("vitals_responses");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id");
        builder.Property(p => p.EngineerId).HasColumnName("engineer_id").IsRequired();
        builder.Property(p => p.Score).HasColumnName("score").IsRequired();
        builder.Property(p => p.Comment).HasColumnName("comment");
        builder.Property(p => p.WeekOf).HasColumnName("week_of").IsRequired();
        builder.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(p => new { p.EngineerId, p.WeekOf }).IsUnique().HasDatabaseName("ix_vitals_responses_engineer_week");
    }
}
