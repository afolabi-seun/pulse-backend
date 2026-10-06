using Pulse.Domain.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class SlackInstallationConfiguration : IEntityTypeConfiguration<SlackInstallation>
{
    public void Configure(EntityTypeBuilder<SlackInstallation> builder)
    {
        builder.ToTable("slack_installations");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id");
        builder.Property(s => s.OrganizationId).HasColumnName("organization_id").IsRequired();
        builder.Property(s => s.TeamId).HasColumnName("team_id").HasMaxLength(32).IsRequired();
        builder.Property(s => s.TeamName).HasColumnName("team_name").HasMaxLength(200).IsRequired();
        builder.Property(s => s.BotUserId).HasColumnName("bot_user_id").HasMaxLength(32).IsRequired();
        builder.Property(s => s.EncryptedBotToken).HasColumnName("encrypted_bot_token").HasColumnType("text").IsRequired();
        builder.Property(s => s.InstalledByEngineerId).HasColumnName("installed_by_engineer_id").IsRequired();
        builder.Property(s => s.CreatedAt).HasColumnName("created_at").IsRequired();

        // One connection per organization, and one organization per workspace (events carry only team_id).
        builder.HasIndex(s => s.OrganizationId).IsUnique().HasDatabaseName("ux_slack_installations_organization_id");
        builder.HasIndex(s => s.TeamId).IsUnique().HasDatabaseName("ux_slack_installations_team_id");

        builder.HasOne<Pulse.Domain.Organizations.Organization>()
            .WithMany()
            .HasForeignKey(s => s.OrganizationId)
            .HasConstraintName("fk_slack_installations_organizations_organization_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
