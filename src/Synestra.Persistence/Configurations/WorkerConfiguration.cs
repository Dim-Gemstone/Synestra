using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Synestra.Domain.Workers;

namespace Synestra.Persistence.Configurations;

internal sealed class WorkerConfiguration : IEntityTypeConfiguration<Worker>
{
    public void Configure(EntityTypeBuilder<Worker> builder)
    {
        builder.ToTable("workers");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.Capacity).HasColumnName("capacity").IsRequired();
        builder.Property(x => x.RegisteredAtUtc).HasColumnName("registered_at_utc").IsRequired();
        builder.Property(x => x.LastSeenAtUtc).HasColumnName("last_seen_at_utc").IsRequired();
    }
}
