using Pulse.Domain.CheckIns;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class CheckInConfiguration : IEntityTypeConfiguration<CheckIn>
{
    public void Configure(EntityTypeBuilder<CheckIn> builder)
    {
        builder.ToTable("check_ins");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id");
        builder.Property(c => c.EngineerId).HasColumnName("engineer_id").IsRequired();
        builder.Property(c => c.Date).HasColumnName("date").IsRequired();
        builder.Property(c => c.Completed).HasColumnName("work_completed").HasMaxLength(2000).IsRequired();
        builder.Property(c => c.PlannedNext).HasColumnName("planned").HasMaxLength(2000).IsRequired();
        builder.Property(c => c.Blockers).HasColumnName("blockers").HasMaxLength(2000);
        builder.Property(c => c.ProjectId).HasColumnName("project_id");
        builder.Property(c => c.CreatedAt).HasColumnName("submitted_at").IsRequired();

        // One check-in per engineer per calendar date per project (null = no specific project)
        builder.HasIndex(c => new { c.EngineerId, c.Date, c.ProjectId }).IsUnique()
            .HasDatabaseName("ix_check_ins_engineer_id_date_project_id");
    }
}
