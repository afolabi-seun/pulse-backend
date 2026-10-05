using Pulse.Domain.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class FailedEmailConfiguration : IEntityTypeConfiguration<FailedEmail>
{
    public void Configure(EntityTypeBuilder<FailedEmail> builder)
    {
        builder.ToTable("failed_emails");
        builder.HasKey(e => e.Id);
        OrganizationConfiguration.ConfigureOrganizationId(builder, "failed_emails");
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.To).HasColumnName("to").HasMaxLength(320).IsRequired();
        builder.Property(e => e.Subject).HasColumnName("subject").HasMaxLength(998).IsRequired();
        builder.Property(e => e.HtmlBody).HasColumnName("html_body").IsRequired();
        builder.Property(e => e.AttemptCount).HasColumnName("attempt_count").IsRequired();
        builder.Property(e => e.LastAttemptAt).HasColumnName("last_attempt_at").IsRequired();
        builder.Property(e => e.LastError).HasColumnName("last_error");
        builder.Property(e => e.IsResolved).HasColumnName("is_resolved").IsRequired();
        builder.Property(e => e.ResolvedAt).HasColumnName("resolved_at");

        builder.HasIndex(e => new { e.IsResolved, e.CreatedAt })
            .HasDatabaseName("ix_failed_emails_is_resolved_created_at");
    }
}
