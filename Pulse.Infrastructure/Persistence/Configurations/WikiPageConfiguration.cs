using Pulse.Domain.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class WikiPageConfiguration : IEntityTypeConfiguration<WikiPage>
{
    public void Configure(EntityTypeBuilder<WikiPage> builder)
    {
        builder.ToTable("wiki_pages");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id");
        builder.Property(p => p.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(p => p.Title).HasColumnName("title").HasMaxLength(300).IsRequired();
        builder.Property(p => p.Content).HasColumnName("content").IsRequired();
        builder.Property(p => p.AuthorId).HasColumnName("author_id").IsRequired();
        builder.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at");
        builder.Property(p => p.RestrictedToMembers).HasColumnName("restricted_to_members").IsRequired().HasDefaultValue(false);

        builder.HasIndex(p => p.ProjectId).HasDatabaseName("ix_wiki_pages_project_id");
    }
}
