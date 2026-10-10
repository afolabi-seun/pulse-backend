using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class PersonalChatSettingsConfiguration : IEntityTypeConfiguration<PersonalChatSettings>
{
    public void Configure(EntityTypeBuilder<PersonalChatSettings> builder)
    {
        builder.ToTable("personal_chat_settings");
        builder.HasKey(s => s.EngineerId);
        builder.Property(s => s.EngineerId).HasColumnName("engineer_id");
        builder.Property(s => s.Channel).HasColumnName("channel").HasMaxLength(20).IsRequired();
        builder.Property(s => s.SlackUserId).HasColumnName("slack_user_id").HasMaxLength(32);
        builder.Property(s => s.GoogleChatDmSpace).HasColumnName("google_chat_dm_space").HasMaxLength(128);
        builder.Property(s => s.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasOne<Engineer>()
            .WithMany()
            .HasForeignKey(s => s.EngineerId)
            .HasConstraintName("fk_personal_chat_settings_engineers_engineer_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
