using System.Reflection;
using System.Text.Json;
using Synestra.Domain.Jobs;
using Xunit;

namespace Synestra.Domain.Tests.Jobs;

public sealed class AbandonAttemptTests
{
    private static readonly DateTime Now = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Abandon_RecordsLossAndPreservesJobIdentityAndHistory()
    {
        var job = CreateJob();
        var attempt = job.StartAttempt(Now);
        var identity = new { job.Id, job.JobDefinitionId, job.Type, job.Payload, job.MaxAttempts, job.AvailableAtUtc };
        var finished = Now.AddMinutes(1);
        job.AbandonAttempt(attempt, finished);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(JobAttemptStatus.Abandoned, attempt.Status);
        Assert.Equal(finished, job.CompletedAtUtc);
        Assert.Equal(finished, attempt.FinishedAtUtc);
        Assert.Equal(Now, attempt.StartedAtUtc);
        Assert.Null(attempt.Result);
        Assert.Equal("execution_lease_expired", attempt.ErrorCode);
        Assert.Equal("Execution lease expired before completion was recorded.", attempt.ErrorMessage);
        Assert.Same(attempt, Assert.Single(job.Attempts));
        Assert.Equal(identity, new { job.Id, job.JobDefinitionId, job.Type, job.Payload, job.MaxAttempts, job.AvailableAtUtc });

        var terminal = JsonSerializer.Serialize(job);
        Assert.Throws<InvalidOperationException>(() => job.AbandonAttempt(attempt, finished.AddSeconds(1)));
        Assert.Throws<InvalidOperationException>(() => job.SucceedAttempt(attempt, "{}", finished.AddSeconds(1)));
        Assert.Throws<InvalidOperationException>(() => job.FailAttempt(attempt, "other", "other", finished.AddSeconds(1)));
        Assert.Throws<InvalidOperationException>(() => job.StartAttempt(finished.AddSeconds(1)));
        Assert.Equal(terminal, JsonSerializer.Serialize(job));
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Abandon_RejectsNonUtcWithoutMutation(DateTimeKind kind)
    {
        var job = CreateJob();
        var attempt = job.StartAttempt(Now);
        var before = JsonSerializer.Serialize(job);
        Assert.Throws<ArgumentException>(() => job.AbandonAttempt(attempt, DateTime.SpecifyKind(Now, kind)));
        Assert.Equal(before, JsonSerializer.Serialize(job));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void Abandon_RejectsTimeBeforeCreationOrAttemptStart(int seconds)
    {
        var job = CreateJob();
        var attempt = job.StartAttempt(Now.AddSeconds(1));
        var before = JsonSerializer.Serialize(job);
        Assert.Throws<ArgumentOutOfRangeException>(() => job.AbandonAttempt(attempt, Now.AddSeconds(seconds)));
        Assert.Equal(before, JsonSerializer.Serialize(job));
    }

    [Fact]
    public void Abandon_RequiresAttemptMembershipIncludingTheTrackedInstance()
    {
        var job = CreateJob();
        var attempt = job.StartAttempt(Now);
        var foreign = CreateJob().StartAttempt(Now);
        var before = JsonSerializer.Serialize(job);
        Assert.Throws<ArgumentNullException>(() => job.AbandonAttempt(null!, Now));
        Assert.Throws<ArgumentException>(() => job.AbandonAttempt(foreign, Now));
        Set(foreign, nameof(JobAttempt.JobId), job.Id);
        Set(foreign, nameof(JobAttempt.Id), attempt.Id);
        Assert.Throws<ArgumentException>(() => job.AbandonAttempt(foreign, Now));
        Assert.Equal(before, JsonSerializer.Serialize(job));
        Assert.Null(foreign.FinishedAtUtc);
    }

    [Fact]
    public void Abandon_RejectsEveryNonRunningStateWithoutPartialMutation()
    {
        foreach (var state in Enum.GetValues<JobStatus>().Where(state => state != JobStatus.Running))
        {
            var job = CreateJob();
            var attempt = job.StartAttempt(Now);
            Set(job, nameof(Job.Status), state);
            RejectWithoutMutation(job, attempt);
        }
        foreach (var state in Enum.GetValues<JobAttemptStatus>().Where(state => state != JobAttemptStatus.Running))
        {
            var job = CreateJob();
            var attempt = job.StartAttempt(Now);
            Set(attempt, nameof(JobAttempt.Status), state);
            RejectWithoutMutation(job, attempt);
        }
    }

    [Theory]
    [InlineData("CompletedAtUtc")]
    [InlineData("FinishedAtUtc")]
    [InlineData("Result")]
    [InlineData("ErrorCode")]
    [InlineData("ErrorMessage")]
    public void Abandon_RejectsInconsistentRunningOutcomeWithoutOverwriting(string property)
    {
        var job = CreateJob();
        var attempt = job.StartAttempt(Now);
        Set(property == "CompletedAtUtc" ? job : attempt, property,
            property.EndsWith("Utc", StringComparison.Ordinal) ? Now : "existing");
        RejectWithoutMutation(job, attempt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Abandon_CannotOverwriteWorkerCompletion(bool success)
    {
        var job = CreateJob();
        var attempt = job.StartAttempt(Now);
        if (success) job.SucceedAttempt(attempt, "{}", Now);
        else job.FailAttempt(attempt, "error", "message", Now);
        RejectWithoutMutation(job, attempt);
    }

    [Fact]
    public void AttemptHasNoPublicAbandonMethodOrSetters()
    {
        Assert.DoesNotContain(typeof(JobAttempt).GetMethods(BindingFlags.Public | BindingFlags.Instance), method => method.Name == "Abandon");
        Assert.DoesNotContain(typeof(JobAttempt).GetProperties(), property => property.SetMethod?.IsPublic == true);
    }

    private static void RejectWithoutMutation(Job job, JobAttempt attempt)
    {
        var before = JsonSerializer.Serialize(job);
        Assert.Throws<InvalidOperationException>(() => job.AbandonAttempt(attempt, Now.AddSeconds(40)));
        Assert.Equal(before, JsonSerializer.Serialize(job));
    }
    private static void Set(object entity, string property, object value) => entity.GetType().GetProperty(property)!.SetValue(entity, value);
    private static Job CreateJob() => new(Guid.CreateVersion7(), "test", "{}", 2, 3, Now, Now);
}
