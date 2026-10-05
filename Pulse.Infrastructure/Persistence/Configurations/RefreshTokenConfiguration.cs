using Pulse.Domain.Engineers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.EngineerID).HasColumnName("engineer_id").IsRequired();
        builder.Property(r => r.TokenHash).HasColumnName("token_hash").HasMaxLength(256).IsRequired();
        builder.Property(r => r.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(r => r.IsRevoked).HasColumnName("is_revoked").IsRequired();
        builder.Property(r => r.ReplacedByTokenHash).HasColumnName("replaced_by_token_hash").HasMaxLength(256);
        builder.Property(r => r.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(r => r.LastUsedAt).HasColumnName("last_used_at").IsRequired();

        // The primary lookup for every token refresh request
        builder.HasIndex(r => r.TokenHash).IsUnique().HasDatabaseName("ix_refresh_tokens_token_hash");
        builder.HasIndex(r => new { r.EngineerID, r.IsRevoked })
            .HasDatabaseName("ix_refresh_tokens_engineer_id_is_revoked");
    }
}
