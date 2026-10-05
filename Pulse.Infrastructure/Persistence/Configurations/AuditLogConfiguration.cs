using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pulse.Infrastructure.Persistence.Configurations;

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
    {
        builder.ToTable("audit_log");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id").UseIdentityColumn();
        builder.Property(a => a.Action).HasColumnName("action").HasMaxLength(100).IsRequired();
        builder.Property(a => a.ActorId).HasColumnName("actor_id").IsRequired();
        builder.Property(a => a.IpAddress).HasColumnName("ip_address").HasMaxLength(45);
        builder.Property(a => a.Detail).HasColumnName("detail");
        builder.Property(a => a.Ts).HasColumnName("ts").IsRequired();

        builder.HasIndex(a => a.Ts).HasDatabaseName("ix_audit_log_ts");
        builder.HasIndex(a => a.ActorId).HasDatabaseName("ix_audit_log_actor_id");
    }
}
