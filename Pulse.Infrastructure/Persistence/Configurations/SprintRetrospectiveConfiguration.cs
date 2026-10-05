using Pulse.Domain.Sprints;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class SprintRetrospectiveConfiguration : IEntityTypeConfiguration<SprintRetrospective>
{
    public void Configure(EntityTypeBuilder<SprintRetrospective> builder)
    {
        builder.ToTable("sprint_retrospectives");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.SprintId).HasColumnName("sprint_id").IsRequired();
        builder.Property(r => r.CreatedById).HasColumnName("created_by_id").IsRequired();
        builder.Property(r => r.WentWell).HasColumnName("went_well").HasMaxLength(5000).IsRequired();
        builder.Property(r => r.NeedsImprovement).HasColumnName("needs_improvement").HasMaxLength(5000).IsRequired();
        builder.Property(r => r.ActionItems).HasColumnName("action_items").HasMaxLength(5000).IsRequired();
        builder.Property(r => r.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(r => r.UpdatedAt).HasColumnName("updated_at");
        builder.HasIndex(r => r.SprintId).IsUnique().HasDatabaseName("ix_sprint_retrospectives_sprint_id");
    }
}
