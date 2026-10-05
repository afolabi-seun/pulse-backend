using Pulse.Domain.Overrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class OverworkOverrideConfiguration : IEntityTypeConfiguration<OverworkOverride>
{
    public void Configure(EntityTypeBuilder<OverworkOverride> builder)
    {
        builder.ToTable("overwork_overrides");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).HasColumnName("id");
        builder.Property(o => o.EngineerId).HasColumnName("engineer_id").IsRequired();
        builder.Property(o => o.Reason).HasColumnName("reason").HasMaxLength(1000).IsRequired();
        builder.Property(o => o.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(o => o.GrantedById).HasColumnName("granted_by").IsRequired();
        builder.Property(o => o.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(o => new { o.EngineerId, o.ExpiresAt })
            .HasDatabaseName("ix_overwork_overrides_engineer_id_expires_at");
    }
}
