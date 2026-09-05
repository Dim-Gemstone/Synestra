using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Synestra.Application.Jobs;
using Synestra.Domain.Jobs;
using Synestra.Persistence.Jobs;

namespace Synestra.Persistence.Configurations;

internal sealed class JobSubmissionConfiguration : IEntityTypeConfiguration<JobSubmissionRecord>
{
    public void Configure(EntityTypeBuilder<JobSubmissionRecord> builder)
    {
        builder.ToTable("job_submissions", table => table.HasCheckConstraint(
            "CK_job_submissions_key_format", "key <> '' AND key COLLATE \"C\" !~ '[^!-~]' AND position(',' in key) = 0"));
        builder.HasKey(x => x.Key);
        builder.Property(x => x.Key).HasColumnName("key")
            .HasMaxLength(SubmitJob.MaximumIdempotencyKeyLength).UseCollation("C");
        builder.Property(x => x.JobId).HasColumnName("job_id");
        builder.Property(x => x.Identity).HasColumnName("request_identity").HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                value => JsonSerializer.Deserialize<SubmitJobIdentity>(value, (JsonSerializerOptions?)null)!)
            .IsRequired();
        builder.Property(x => x.Response).HasColumnName("response").HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                value => JsonSerializer.Deserialize<JobDetails>(value, (JsonSerializerOptions?)null)!)
            .IsRequired();
        builder.HasOne<Job>().WithOne().HasForeignKey<JobSubmissionRecord>(x => x.JobId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
