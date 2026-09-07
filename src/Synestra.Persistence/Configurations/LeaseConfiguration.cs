using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Synestra.Domain.Jobs;
using Synestra.Domain.Leases;
using Synestra.Domain.Workers;
using Synestra.Persistence.Workers;

namespace Synestra.Persistence.Configurations;

internal sealed class LeaseConfiguration : IEntityTypeConfiguration<Lease>
{
    public void Configure(EntityTypeBuilder<Lease> builder)
    {
        builder.ToTable("leases", table =>
        {
            table.HasCheckConstraint("CK_leases_session_id",
                "session_id IS NULL OR (get_byte(uuid_send(session_id), 6) >> 4 = 7 AND (get_byte(uuid_send(session_id), 8) & 192) = 128)");
            table.HasCheckConstraint("CK_leases_token_hash", "token_hash IS NULL OR octet_length(token_hash) = 32");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.JobAttemptId).HasColumnName("job_attempt_id").IsRequired();
        builder.Property(x => x.WorkerId).HasColumnName("worker_id").IsRequired();
        builder.Property(x => x.SessionId).HasColumnName("session_id");
        builder.Property<byte[]?>("TokenHash").HasColumnName("token_hash").HasColumnType("bytea").HasMaxLength(32);
        builder.Property(x => x.AcquiredAtUtc).HasColumnName("acquired_at_utc").IsRequired();
        builder.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc").IsRequired();
        builder.Property(x => x.ReleasedAtUtc).HasColumnName("released_at_utc");
        builder.HasIndex(x => x.JobAttemptId).IsUnique();
        builder.HasIndex(x => x.WorkerId);
        builder.HasIndex(x => new { x.ExpiresAtUtc, x.Id })
            .HasDatabaseName("IX_leases_expiration_unreleased")
            .HasFilter("released_at_utc IS NULL");
        builder.HasOne<JobAttempt>().WithOne(x => x.Lease).HasForeignKey<Lease>(x => x.JobAttemptId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Worker>().WithMany().HasForeignKey(x => x.WorkerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WorkerSessionRecord>().WithMany().HasForeignKey(x => new { x.WorkerId, x.SessionId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
