using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Synestra.Domain.Workers;
using Synestra.Persistence.Workers;

namespace Synestra.Persistence.Configurations;

internal sealed class WorkerSessionConfiguration : IEntityTypeConfiguration<WorkerSessionRecord>
{
    public void Configure(EntityTypeBuilder<WorkerSessionRecord> builder)
    {
        builder.ToTable("worker_sessions");
        builder.HasKey(x => new { x.WorkerId, x.SessionId });
        builder.Property(x => x.WorkerId).HasColumnName("worker_id");
        builder.Property(x => x.SessionId).HasColumnName("session_id");
        builder.HasOne<Worker>().WithMany().HasForeignKey(x => x.WorkerId).OnDelete(DeleteBehavior.Cascade);
    }
}
