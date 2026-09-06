using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Synestra.Domain.Jobs;

namespace Synestra.Persistence.Configurations;

internal sealed class JobAttemptConfiguration : IEntityTypeConfiguration<JobAttempt>
{
    public void Configure(EntityTypeBuilder<JobAttempt> builder)
    {
        builder.ToTable("job_attempts", table =>
        {
            table.HasCheckConstraint("CK_job_attempts_completion_report_id",
                "completion_report_id IS NULL OR (get_byte(uuid_send(completion_report_id), 6) >> 4 = 7 AND (get_byte(uuid_send(completion_report_id), 8) & 192) = 128)");
            table.HasCheckConstraint("CK_job_attempts_completion_snapshot",
                "(completion_report_id IS NULL) = (completion_snapshot IS NULL) AND (completion_snapshot IS NULL OR jsonb_typeof(completion_snapshot) = 'object')");
            table.HasCheckConstraint("CK_job_attempts_result", "result IS NULL OR jsonb_typeof(result) = 'object'");
            table.HasCheckConstraint("CK_job_attempts_reported_completion", """
                completion_report_id IS NULL OR (
                  finished_at_utc IS NOT NULL AND finished_at_utc >= started_at_utc AND (
                    (status = 'Succeeded' AND result IS NOT NULL AND error_code IS NULL AND error_message IS NULL) OR
                    (status = 'Failed' AND result IS NULL AND error_code IS NOT NULL AND error_message IS NOT NULL
                      AND error_code ~ '[^[:space:]]' AND error_message ~ '[^[:space:]]')))
                """);
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.JobId).HasColumnName("job_id").IsRequired();
        builder.Property(x => x.Number).HasColumnName("number").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.StartedAtUtc).HasColumnName("started_at_utc").IsRequired();
        builder.Property(x => x.FinishedAtUtc).HasColumnName("finished_at_utc");
        builder.Property(x => x.Result).HasColumnName("result").HasColumnType("jsonb");
        builder.Property<Guid?>("CompletionReportId").HasColumnName("completion_report_id");
        builder.Property<string?>("CompletionSnapshot").HasColumnName("completion_snapshot").HasColumnType("jsonb");
        builder.Property(x => x.ErrorCode).HasColumnName("error_code").HasMaxLength(100);
        builder.Property(x => x.ErrorMessage).HasColumnName("error_message").HasMaxLength(2000);
        builder.HasIndex(x => new { x.JobId, x.Number }).IsUnique();
    }
}
