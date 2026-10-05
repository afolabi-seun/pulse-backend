using Pulse.Domain.Alerts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class GoogleChatSpaceConfiguration : IEntityTypeConfiguration<GoogleChatSpace>
{
    public void Configure(EntityTypeBuilder<GoogleChatSpace> builder)
    {
        builder.ToTable("google_chat_spaces");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id");
        builder.Property(s => s.SpaceId).HasColumnName("space_id").HasMaxLength(200).IsRequired();
        builder.Property(s => s.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
        builder.Property(s => s.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(s => s.SpaceId).IsUnique().HasDatabaseName("ux_google_chat_spaces_space_id");
    }
}
