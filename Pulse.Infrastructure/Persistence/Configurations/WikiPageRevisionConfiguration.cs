using Pulse.Domain.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class WikiPageRevisionConfiguration : IEntityTypeConfiguration<WikiPageRevision>
{
    public void Configure(EntityTypeBuilder<WikiPageRevision> builder)
    {
        builder.ToTable("wiki_page_revisions");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.WikiPageId).HasColumnName("wiki_page_id").IsRequired();
        builder.Property(r => r.Title).HasColumnName("title").HasMaxLength(300).IsRequired();
        builder.Property(r => r.Content).HasColumnName("content").IsRequired();
        builder.Property(r => r.AuthorId).HasColumnName("author_id").IsRequired();
        builder.Property(r => r.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasOne<WikiPage>()
            .WithMany()
            .HasForeignKey(r => r.WikiPageId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => r.WikiPageId).HasDatabaseName("ix_wiki_page_revisions_wiki_page_id");
    }
}
