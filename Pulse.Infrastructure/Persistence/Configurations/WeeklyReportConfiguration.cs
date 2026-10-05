using Pulse.Domain.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class WeeklyReportConfiguration : IEntityTypeConfiguration<WeeklyReport>
{
    public void Configure(EntityTypeBuilder<WeeklyReport> builder)
    {
        builder.ToTable("weekly_reports");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.TeamId).HasColumnName("team_id").IsRequired();
        builder.Property(r => r.WeekOf).HasColumnName("week_of").IsRequired();
        builder.Property(r => r.AuthorId).HasColumnName("author_id").IsRequired();
        builder.Property(r => r.ExecutiveSummary).HasColumnName("executive_summary").HasMaxLength(4000).IsRequired();
        builder.Property(r => r.KeyAccomplishments).HasColumnName("key_accomplishments").HasMaxLength(4000).IsRequired();
        builder.Property(r => r.PlannedNextWeek).HasColumnName("planned_next_week").HasMaxLength(4000).IsRequired();
        builder.Property(r => r.ResourcingNotes).HasColumnName("resourcing_notes").HasMaxLength(4000).IsRequired();
        builder.Property(r => r.SubmittedById).HasColumnName("submitted_by_id");
        builder.Property(r => r.SubmittedAt).HasColumnName("submitted_at");
        builder.Property(r => r.UpdatedAt).HasColumnName("updated_at");
        builder.Property(r => r.CreatedAt).HasColumnName("created_at").IsRequired();

        // One report per team per week
        builder.HasIndex(r => new { r.TeamId, r.WeekOf }).IsUnique()
            .HasDatabaseName("ix_weekly_reports_team_id_week_of");
    }
}
