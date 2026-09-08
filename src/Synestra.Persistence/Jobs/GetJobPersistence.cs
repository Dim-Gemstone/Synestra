using Microsoft.EntityFrameworkCore;
using Synestra.Application.Jobs;
using Synestra.Domain.Jobs;

namespace Synestra.Persistence.Jobs;

internal sealed class GetJobPersistence(SynestraDbContext dbContext) : IGetJobPersistence
{
    public async Task<GetJobDetails?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        // One statement observes Job and latest attempt at the same committed snapshot.
        var row = await dbContext.Database.SqlQuery<JobRow>($"""
            SELECT j.id AS "Id", j.type AS "Type", j.status AS "Status",
                   j.priority AS "Priority", j.max_attempts AS "MaxAttempts",
                   j.created_at_utc AS "CreatedAtUtc", j.available_at_utc AS "AvailableAtUtc",
                   j.completed_at_utc AS "CompletedAtUtc",
                   a.id AS "AttemptId", a.number AS "AttemptNumber", a.status AS "Outcome",
                   CASE WHEN a.status = 'Succeeded' THEN
                       CASE WHEN a.completion_snapshot IS NOT NULL THEN a.completion_snapshot ->> 'Result'
                            WHEN octet_length(a.result::text) <= 65536 THEN a.result::text
                       END
                   END AS "Result",
                   CASE WHEN a.status IN ('Failed', 'Abandoned')
                             AND a.error_code IS NOT NULL AND a.error_message IS NOT NULL
                        THEN a.error_code END AS "ErrorCode",
                   CASE WHEN a.status IN ('Failed', 'Abandoned')
                             AND a.error_code IS NOT NULL AND a.error_message IS NOT NULL
                        THEN a.error_message END AS "ErrorMessage"
            FROM jobs AS j
            LEFT JOIN LATERAL (
                SELECT attempt.id, attempt.number, attempt.status, attempt.finished_at_utc,
                       attempt.result, attempt.completion_snapshot, attempt.error_code, attempt.error_message
                FROM job_attempts AS attempt
                WHERE attempt.job_id = j.id
                ORDER BY attempt.number DESC
                LIMIT 1
            ) AS a ON j.completed_at_utc IS NOT NULL AND a.finished_at_utc = j.completed_at_utc
                AND ((j.status = 'Succeeded' AND a.status = 'Succeeded')
                    OR (j.status = 'Failed' AND a.status IN ('Failed', 'Abandoned')))
            WHERE j.id = {id}
            """).SingleOrDefaultAsync(cancellationToken);

        if (row is null) return null;
        var completion = row.AttemptId is { } attemptId
            ? new JobCompletionDetails(attemptId, row.AttemptNumber!.Value,
                Enum.Parse<JobAttemptStatus>(row.Outcome!), row.Result,
                row.ErrorCode is { } code && row.ErrorMessage is { } message
                    ? new JobCompletionError(code, message) : null)
            : null;
        return new(row.Id, row.Type, Enum.Parse<JobStatus>(row.Status), row.Priority,
            row.MaxAttempts, row.CreatedAtUtc, row.AvailableAtUtc, row.CompletedAtUtc, completion);
    }

    private sealed class JobRow
    {
        public Guid Id { get; init; }
        public string Type { get; init; } = null!;
        public string Status { get; init; } = null!;
        public int Priority { get; init; }
        public int MaxAttempts { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public DateTime AvailableAtUtc { get; init; }
        public DateTime? CompletedAtUtc { get; init; }
        public Guid? AttemptId { get; init; }
        public int? AttemptNumber { get; init; }
        public string? Outcome { get; init; }
        public string? Result { get; init; }
        public string? ErrorCode { get; init; }
        public string? ErrorMessage { get; init; }
    }
}
