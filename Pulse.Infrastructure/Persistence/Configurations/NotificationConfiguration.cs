using Pulse.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).HasColumnName("id");
        builder.Property(n => n.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(n => n.Kind).HasColumnName("kind").HasMaxLength(50).IsRequired();
        builder.Property(n => n.Payload).HasColumnName("payload").HasColumnType("jsonb");
        builder.Property(n => n.Channel).HasColumnName("channel").HasMaxLength(20).IsRequired();
        builder.Property(n => n.ReadAt).HasColumnName("read_at");
        builder.Property(n => n.CreatedAt).HasColumnName("sent_at").IsRequired();

        builder.HasIndex(n => new { n.UserId, n.CreatedAt })
            .HasDatabaseName("ix_notifications_user_id_sent_at");
    }
}
