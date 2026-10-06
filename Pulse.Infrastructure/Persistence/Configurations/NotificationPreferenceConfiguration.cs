using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class NotificationPreferenceConfiguration : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> builder)
    {
        builder.ToTable("notification_preferences");
        builder.HasKey(p => new { p.EngineerId, p.Kind });
        builder.Property(p => p.EngineerId).HasColumnName("engineer_id");
        builder.Property(p => p.Kind).HasColumnName("kind").HasMaxLength(64);
        builder.Property(p => p.EmailEnabled).HasColumnName("email_enabled").IsRequired();
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasOne<Engineer>()
            .WithMany()
            .HasForeignKey(p => p.EngineerId)
            .HasConstraintName("fk_notification_preferences_engineers_engineer_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
