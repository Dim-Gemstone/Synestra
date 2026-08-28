using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Synestra.Domain.Jobs;
using Synestra.Domain.Leases;
using Synestra.Domain.Workers;

namespace Synestra.Persistence.Configurations;

internal sealed class LeaseConfiguration : IEntityTypeConfiguration<Lease>
{
    public void Configure(EntityTypeBuilder<Lease> builder)
    {
        builder.ToTable("leases");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.JobAttemptId).HasColumnName("job_attempt_id").IsRequired();
        builder.Property(x => x.WorkerId).HasColumnName("worker_id").IsRequired();
        builder.Property(x => x.AcquiredAtUtc).HasColumnName("acquired_at_utc").IsRequired();
        builder.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc").IsRequired();
        builder.Property(x => x.ReleasedAtUtc).HasColumnName("released_at_utc");
        builder.HasIndex(x => x.JobAttemptId).IsUnique();
        builder.HasIndex(x => x.WorkerId);
        builder.HasOne<JobAttempt>().WithOne(x => x.Lease).HasForeignKey<Lease>(x => x.JobAttemptId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Worker>().WithMany().HasForeignKey(x => x.WorkerId).OnDelete(DeleteBehavior.Restrict);
    }
}
