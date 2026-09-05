using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Synestra.Domain.Workers;

namespace Synestra.Persistence.Configurations;

internal sealed class WorkerSupportedTypeConfiguration : IEntityTypeConfiguration<WorkerSupportedType>
{
    public void Configure(EntityTypeBuilder<WorkerSupportedType> builder)
    {
        builder.ToTable("worker_supported_types", table => table.HasCheckConstraint(
            "CK_worker_supported_types_type", "type ~ '[^[:space:]]'"));
        builder.HasKey(x => new { x.WorkerId, x.Type });
        builder.Property(x => x.WorkerId).HasColumnName("worker_id");
        builder.Property(x => x.Type).HasColumnName("type").HasMaxLength(100).UseCollation("C");
    }
}
