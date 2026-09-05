using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Synestra.Domain.Workers;

namespace Synestra.Persistence.Configurations;

internal sealed class WorkerConfiguration : IEntityTypeConfiguration<Worker>
{
    public void Configure(EntityTypeBuilder<Worker> builder)
    {
        builder.ToTable("workers", table =>
        {
            table.HasCheckConstraint("CK_workers_capacity", "capacity > 0");
            table.HasCheckConstraint("CK_workers_session", "(session_id IS NULL) = (session_started_at_utc IS NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.Capacity).HasColumnName("capacity").IsRequired();
        builder.Property(x => x.SessionId).HasColumnName("session_id");
        builder.Property(x => x.SessionStartedAtUtc).HasColumnName("session_started_at_utc");
        builder.Property(x => x.RegisteredAtUtc).HasColumnName("registered_at_utc").IsRequired();
        builder.Property(x => x.LastSeenAtUtc).HasColumnName("last_seen_at_utc").IsRequired();
        builder.HasMany(x => x.SupportedTypes).WithOne().HasForeignKey(x => x.WorkerId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.SupportedTypes).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
