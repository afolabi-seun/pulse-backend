using Pulse.Domain.Engineers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class EngineerConfiguration : IEntityTypeConfiguration<Engineer>
{
    public void Configure(EntityTypeBuilder<Engineer> builder)
    {
        builder.ToTable("engineers");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(e => e.Email).HasColumnName("email").HasMaxLength(320).IsRequired();
        builder.Property(e => e.PasswordHash).HasColumnName("password_hash").IsRequired();
        builder.Property(e => e.Role).HasColumnName("role").HasMaxLength(30).IsRequired();
        builder.Property(e => e.BaselinePoints).HasColumnName("baseline_points").IsRequired();
        builder.Property(e => e.BaselineCycleDays).HasColumnName("baseline_cycle_days").IsRequired();
        builder.Property(e => e.Team).HasColumnName("team").HasMaxLength(100);
        builder.Property(e => e.TeamId).HasColumnName("team_id");
        builder.Property(e => e.IsQa).HasColumnName("is_qa").IsRequired().HasDefaultValue(false);
        builder.Property(e => e.Discipline).HasColumnName("discipline")
            .HasConversion<string>().HasMaxLength(20);
        builder.Property(e => e.IsActive).HasColumnName("is_active").IsRequired();
        builder.Property(e => e.FailedLoginAttempts).HasColumnName("failed_login_attempts").IsRequired();
        builder.Property(e => e.LockedUntil).HasColumnName("locked_until");
        builder.Property(e => e.PasswordResetToken).HasColumnName("password_reset_token");
        builder.Property(e => e.PasswordResetTokenExpiresAt).HasColumnName("password_reset_token_expires_at");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(e => e.Email).IsUnique().HasDatabaseName("ix_engineers_email");
    }
}
