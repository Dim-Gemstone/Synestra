using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Synestra.Application.Jobs;
using Synestra.Application.Workers;
using Synestra.Domain.Jobs;
using Xunit;

namespace Synestra.Persistence.IntegrationTests.Workers;

public sealed partial class ExecutionPersistenceTests
{
    [Theory]
    [InlineData("succeeded")]
    [InlineData("failed")]
    public async Task ClientRead_ReturnsCommittedReport(string outcome)
    {
        var execution = await AcquireAsync();
        var reported = await CompleteAsync(execution, Report(outcome), Now.AddSeconds(10));
        var job = await ReadJobAsync(execution.Work.JobId);

        Assert.Equal(reported.Completion!.FinishedAtUtc, job.CompletedAtUtc);
        var completion = Assert.IsType<JobCompletionDetails>(job.Completion);
        Assert.Equal(execution.Work.AttemptId, completion.AttemptId);
        Assert.Equal(1, completion.AttemptNumber);
        Assert.Equal(outcome == "succeeded" ? JobAttemptStatus.Succeeded : JobAttemptStatus.Failed, completion.Outcome);
        Assert.Equal(reported.Completion.Result, completion.Result);
        Assert.Equal(reported.Completion.Error?.Code, completion.Error?.Code);
        Assert.Equal(reported.Completion.Error?.Message, completion.Error?.Message);
    }

    [Theory]
    [InlineData("Succeeded", null, null)]
    [InlineData("Failed", null, null)]
    [InlineData("Failed", "partial_code", null)]
    [InlineData("Failed", null, "partial message")]
    [InlineData("Abandoned", "execution_lease_expired", "Stored loss.")]
    public async Task ClientRead_PreservesReportlessLegacyOutcomeAndMissingData(
        string status, string? code, string? message)
    {
        var execution = await AcquireAsync();
        await using var context = Context();
        var jobStatus = status == "Succeeded" ? "Succeeded" : "Failed";
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE jobs SET status = {jobStatus}, completed_at_utc = {Now.UtcDateTime};
            UPDATE job_attempts SET status = {status}, finished_at_utc = {Now.UtcDateTime},
                error_code = {code}, error_message = {message};
            """, Token);

        var job = await ReadJobAsync(execution.Work.JobId);
        Assert.Equal(Now.UtcDateTime, job.CompletedAtUtc);
        var completion = Assert.IsType<JobCompletionDetails>(job.Completion);
        Assert.Equal(Enum.Parse<JobAttemptStatus>(status), completion.Outcome);
        Assert.Null(completion.Result);
        Assert.Equal(code is not null && message is not null ? new JobCompletionError(code, message) : null, completion.Error);
        Assert.Null(await context.JobAttempts.Select(a => EF.Property<Guid?>(a, "CompletionReportId")).SingleAsync(Token));
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData("Running", "Running", false)]
    [InlineData("Succeeded", "Running", true)]
    [InlineData("Succeeded", "Failed", true)]
    [InlineData("Failed", "Succeeded", true)]
    [InlineData("Cancelled", "Succeeded", true)]
    [InlineData("Succeeded", "Succeeded", false)]
    public async Task ClientRead_InconsistentLatestAttemptHasNoCompletion(string jobStatus, string attemptStatus, bool finished)
    {
        var execution = await AcquireAsync();
        await using var context = Context();
        DateTime? completionTime = finished ? Now.UtcDateTime : null;
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE jobs SET status = {jobStatus}, completed_at_utc = {completionTime};
            UPDATE job_attempts SET status = {attemptStatus}, finished_at_utc = {completionTime};
            """, Token);

        var job = await ReadJobAsync(execution.Work.JobId);
        Assert.Equal(Enum.Parse<JobStatus>(jobStatus), job.Status);
        Assert.Equal(completionTime, job.CompletedAtUtc);
        Assert.Null(job.Completion);
    }

    [Fact]
    public async Task ClientRead_SelectsGreatestAttemptNumberBeforeCheckingTerminalAssociation()
    {
        var execution = await AcquireAsync();
        await CompleteAsync(execution, Report(), Now.AddSeconds(10));
        // Attempt number determines recency even when UUID ordering disagrees.
        var laterAttemptId = Guid.Parse("01900000-0000-7000-8000-000000000001");
        await using var context = Context();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO job_attempts (id, job_id, number, status, started_at_utc)
            VALUES ({laterAttemptId}, {execution.Work.JobId}, 6, 'Running', {Now.UtcDateTime});
            """, Token);
        Assert.Null((await ReadJobAsync(execution.Work.JobId)).Completion);

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE job_attempts SET status = 'Succeeded', finished_at_utc = {Now.AddSeconds(9).UtcDateTime}
            WHERE id = {laterAttemptId};
            """, Token);
        Assert.Null((await ReadJobAsync(execution.Work.JobId)).Completion);

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE job_attempts SET finished_at_utc = {Now.AddSeconds(10).UtcDateTime} WHERE id = {laterAttemptId};
            """, Token);
        var completion = Assert.IsType<JobCompletionDetails>((await ReadJobAsync(execution.Work.JobId)).Completion);
        Assert.Equal(laterAttemptId, completion.AttemptId);
        Assert.Equal(6, completion.AttemptNumber);
        Assert.Null(completion.Result);
    }

    [Theory]
    [InlineData(65536, true)]
    [InlineData(65537, false)]
    public async Task ClientRead_BoundsLegacyResultBeforeTransferUsingUtf8Bytes(int bytes, bool available)
    {
        var execution = await AcquireAsync();
        await using var context = Context();
        // jsonb renders this object with a space after the colon: nine ASCII bytes.
        var result = "{\"v\":\"" + new string('é', (bytes - 9) / 2) + new string('a', (bytes - 9) % 2) + "\"}";
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE jobs SET status = 'Succeeded', completed_at_utc = {Now.UtcDateTime};
            UPDATE job_attempts SET status = 'Succeeded', finished_at_utc = {Now.UtcDateTime}, result = {result}::jsonb;
            """, Token);
        Assert.Equal(bytes, await context.Database.SqlQueryRaw<int>(
            "SELECT octet_length(result::text) AS \"Value\" FROM job_attempts").SingleAsync(Token));

        var completion = Assert.IsType<JobCompletionDetails>((await ReadJobAsync(execution.Work.JobId)).Completion);
        if (available) Assert.Equal(bytes, Encoding.UTF8.GetByteCount(Assert.IsType<string>(completion.Result)));
        else Assert.Null(completion.Result);
    }

    [Fact]
    public async Task ClientRead_ProjectsOriginalBoundedResultInOneUntrackedStatement()
    {
        var execution = await AcquireAsync();
        const string result = "{\"n\":1e131071}";
        await CompleteAsync(execution, Report() with { Result = result });
        var capture = new ClientReadCapture();
        await using var provider = Provider(capture);
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SynestraDbContext>();
        var query = new GetJob(scope.ServiceProvider.GetRequiredService<IGetJobPersistence>());

        var response = await query.ExecuteAsync(execution.Work.JobId, Token);
        Assert.Equal(result, response.Job!.Completion!.Result);
        Assert.Equal(1, capture.Commands);
        Assert.False(capture.HasTransaction);
        Assert.DoesNotContain("FOR UPDATE", capture.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload", capture.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "Id", "Type", "Status", "Priority", "MaxAttempts", "CreatedAtUtc", "AvailableAtUtc",
            "CompletedAtUtc", "AttemptId", "AttemptNumber", "Outcome", "Result", "ErrorCode", "ErrorMessage" }.Order(), capture.Columns.Order());
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClientRead_SeesOnlyCommittedTerminalPairWhileWriterHoldsLocks(bool commit)
    {
        var execution = await AcquireAsync();
        await using var scope = _provider.CreateAsyncScope();
        await using var transaction = await scope.ServiceProvider.GetRequiredService<IReportExecutionCompletionPersistence>()
            .BeginTransactionAsync(Token);
        await transaction.LockWorkerAsync(execution.Worker.WorkerId, Token);
        var locked = (await transaction.LockExecutionAsync(execution.Work.LeaseId, Token))!;
        locked.Job!.SucceedAttempt(locked.Attempt!, "{}", Now.AddSeconds(5).UtcDateTime);
        locked.Lease.Release(Now.AddSeconds(5).UtcDateTime);
        await scope.ServiceProvider.GetRequiredService<SynestraDbContext>().SaveChangesAsync(Token);

        var before = await ReadJobAsync(execution.Work.JobId).WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(JobStatus.Running, before.Status);
        Assert.Null(before.CompletedAtUtc);
        Assert.Null(before.Completion);
        if (commit) await transaction.CommitAsync(Token);
        else await transaction.DisposeAsync();

        var after = await ReadJobAsync(execution.Work.JobId);
        Assert.Equal(commit ? JobStatus.Succeeded : JobStatus.Running, after.Status);
        Assert.Equal(commit ? Now.AddSeconds(5).UtcDateTime : (DateTime?)null, after.CompletedAtUtc);
        Assert.Equal(commit ? JobAttemptStatus.Succeeded : (JobAttemptStatus?)null, after.Completion?.Outcome);
    }

    private async Task<GetJobDetails> ReadJobAsync(Guid id)
    {
        await using var scope = _provider.CreateAsyncScope();
        var result = await new GetJob(scope.ServiceProvider.GetRequiredService<IGetJobPersistence>()).ExecuteAsync(id, Token);
        Assert.Equal(GetJobOutcome.Succeeded, result.Outcome);
        return Assert.IsType<GetJobDetails>(result.Job);
    }

    private sealed class ClientReadCapture : DbCommandInterceptor
    {
        public int Commands { get; private set; }
        public bool HasTransaction { get; private set; }
        public string Sql { get; private set; } = "";
        public string[] Columns { get; private set; } = [];
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            Commands++;
            HasTransaction = command.Transaction is not null;
            Sql = command.CommandText;
            Columns = Enumerable.Range(0, result.FieldCount).Select(result.GetName).ToArray();
            return ValueTask.FromResult(result);
        }
    }
}
