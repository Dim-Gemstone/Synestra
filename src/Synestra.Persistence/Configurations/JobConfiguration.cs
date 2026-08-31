using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Synestra.Domain.Jobs;

namespace Synestra.Persistence.Configurations;

internal sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.ToTable("jobs");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.JobDefinitionId).HasColumnName("job_definition_id").IsRequired();
        builder.Property(x => x.Type).HasColumnName("type").HasMaxLength(100).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.Priority).HasColumnName("priority").IsRequired();
        builder.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.MaxAttempts).HasColumnName("max_attempts").IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(x => x.AvailableAtUtc).HasColumnName("available_at_utc").IsRequired();
        builder.Property(x => x.CompletedAtUtc).HasColumnName("completed_at_utc");
        builder
            .HasOne<JobDefinition>()
            .WithMany()
            .HasForeignKey(x => new { x.JobDefinitionId, x.Type })
            .HasPrincipalKey(x => new { x.Id, x.Type })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Attempts).WithOne().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
    }
}
