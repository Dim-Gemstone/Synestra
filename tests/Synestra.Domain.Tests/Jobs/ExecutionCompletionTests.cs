using System.Reflection;
using Synestra.Domain.Jobs;
using Synestra.Domain.Leases;
using Xunit;

namespace Synestra.Domain.Tests.Jobs;

public sealed class ExecutionCompletionTests
{
    private static readonly DateTime Now = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Renewal_ExtendsButNeverShortensAndPreservesAcquisition()
    {
        var lease = CreateLease();
        lease.Renew(Now.AddSeconds(10), TimeSpan.FromSeconds(45));
        Assert.Equal(Now.AddSeconds(55), lease.ExpiresAtUtc);
        lease.Renew(Now.AddSeconds(5), TimeSpan.FromSeconds(1));
        Assert.Equal(Now.AddSeconds(55), lease.ExpiresAtUtc);
        Assert.Equal(Now, lease.AcquiredAtUtc);
        Assert.Null(lease.ReleasedAtUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Renewal_RejectsNonPositiveDurationWithoutMutation(int duration)
    {
        var lease = CreateLease();
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.Renew(Now, TimeSpan.FromSeconds(duration)));
        Assert.Equal(Now.AddSeconds(30), lease.ExpiresAtUtc);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(31)]
    public void Renewal_RejectsExpirationBoundaryAndLater(int seconds)
    {
        var lease = CreateLease();
        Assert.Throws<InvalidOperationException>(() => lease.Renew(Now.AddSeconds(seconds), TimeSpan.FromSeconds(30)));
        Assert.Equal(Now.AddSeconds(30), lease.ExpiresAtUtc);
    }

    [Fact]
    public void Release_AllowsLateTimeButCannotRepeatOrReviveCreateLease()
    {
        var lease = CreateLease();
        lease.Release(Now.AddSeconds(40));
        Assert.Equal(Now.AddSeconds(40), lease.ReleasedAtUtc);
        Assert.Equal(Now.AddSeconds(30), lease.ExpiresAtUtc);
        Assert.Throws<InvalidOperationException>(() => lease.Release(Now.AddSeconds(41)));
        Assert.Throws<InvalidOperationException>(() => lease.Renew(Now.AddSeconds(10), TimeSpan.FromSeconds(30)));
        Assert.Equal(Now.AddSeconds(40), lease.ReleasedAtUtc);
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void EveryExecutionMutation_RequiresExactUtc(DateTimeKind kind)
    {
        var time = DateTime.SpecifyKind(Now, kind);
        var lease = CreateLease();
        var job = CreateJob();
        var attempt = job.StartAttempt(Now);
        Assert.Throws<ArgumentException>(() => lease.Renew(time, TimeSpan.FromSeconds(30)));
        Assert.Throws<ArgumentException>(() => lease.Release(time));
        Assert.Throws<ArgumentException>(() => job.SucceedAttempt(attempt, "{}", time));
        Assert.Throws<ArgumentException>(() => job.FailAttempt(attempt, "error", "message", time));
        AssertRunning(job, attempt);
        Assert.Null(lease.ReleasedAtUtc);
    }

    [Fact]
    public void Times_CannotPrecedeAcquisitionOrStart()
    {
        var lease = CreateLease();
        var job = CreateJob();
        var attempt = job.StartAttempt(Now.AddSeconds(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.Renew(Now.AddTicks(-1), TimeSpan.FromSeconds(30)));
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.Release(Now.AddTicks(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => job.SucceedAttempt(attempt, "{}", Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => job.FailAttempt(attempt, "error", "message", Now));
        AssertRunning(job, attempt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Completion_CoordinatesStatusTimeAndOutcomeAndCannotOverwrite(bool success)
    {
        var job = CreateJob();
        var attempt = job.StartAttempt(Now);
        var lease = new Lease(attempt.Id, Guid.CreateVersion7(), Guid.CreateVersion7(), Now, TimeSpan.FromSeconds(30));
        var finished = Now.AddSeconds(10);
        if (success) job.SucceedAttempt(attempt, "{\"captured\":true}", finished);
        else job.FailAttempt(attempt, "navigation_failed", "The page did not load.", finished);
        lease.Release(finished);
        Assert.Equal(success ? JobStatus.Succeeded : JobStatus.Failed, job.Status);
        Assert.Equal(success ? JobAttemptStatus.Succeeded : JobAttemptStatus.Failed, attempt.Status);
        Assert.Equal(finished, job.CompletedAtUtc);
        Assert.Equal(job.CompletedAtUtc, attempt.FinishedAtUtc);
        Assert.Equal(attempt.FinishedAtUtc, lease.ReleasedAtUtc);
        Assert.Equal(Now, attempt.StartedAtUtc);
        Assert.Equal(Now, lease.AcquiredAtUtc);
        Assert.Equal(success ? "{\"captured\":true}" : null, attempt.Result);
        Assert.Equal(success ? null : "navigation_failed", attempt.ErrorCode);
        Assert.Equal(success ? null : "The page did not load.", attempt.ErrorMessage);
        Assert.Throws<InvalidOperationException>(() => job.SucceedAttempt(attempt, "{}", finished.AddSeconds(1)));
        Assert.Throws<InvalidOperationException>(() => job.FailAttempt(attempt, "different", "different", finished.AddSeconds(1)));
        Assert.Equal(finished, attempt.FinishedAtUtc);
        Assert.All(new[] { job.Id, attempt.Id, lease.Id }, id => Assert.Equal(7, id.Version));
    }

    [Fact]
    public void NonRunningEntitiesAndForeignAttempt_CannotPartlyMutate()
    {
        foreach (var status in Enum.GetValues<JobStatus>().Where(status => status != JobStatus.Running))
        {
            var job = CreateJob();
            var attempt = job.StartAttempt(Now);
            Set(job, nameof(Job.Status), status);
            Assert.Throws<InvalidOperationException>(() => job.SucceedAttempt(attempt, "{}", Now));
            Assert.Throws<InvalidOperationException>(() => job.FailAttempt(attempt, "error", "message", Now));
            Assert.Equal(JobAttemptStatus.Running, attempt.Status);
            Assert.Null(attempt.FinishedAtUtc);
        }
        foreach (var status in Enum.GetValues<JobAttemptStatus>().Where(status => status != JobAttemptStatus.Running))
        {
            var job = CreateJob();
            var attempt = job.StartAttempt(Now);
            Set(attempt, nameof(JobAttempt.Status), status);
            Assert.Throws<InvalidOperationException>(() => job.SucceedAttempt(attempt, "{}", Now));
            Assert.Throws<InvalidOperationException>(() => job.FailAttempt(attempt, "error", "message", Now));
            Assert.Equal(JobStatus.Running, job.Status);
            Assert.Null(job.CompletedAtUtc);
        }
        var running = CreateJob();
        running.StartAttempt(Now);
        var foreign = CreateJob().StartAttempt(Now);
        Assert.Throws<ArgumentException>(() => running.SucceedAttempt(foreign, "{}", Now));
        Assert.Equal(JobAttemptStatus.Running, foreign.Status);
    }

    [Fact]
    public void InvalidOutcomeData_DoesNotPartlyMutate()
    {
        var job = CreateJob();
        var attempt = job.StartAttempt(Now);
        Assert.Throws<ArgumentException>(() => job.SucceedAttempt(attempt, " ", Now));
        foreach (var code in new[] { "", " ", new string('x', 101), "a\0" })
            Assert.Throws<ArgumentException>(() => job.FailAttempt(attempt, code, "message", Now));
        foreach (var message in new[] { "", " ", new string('x', 2001), "a\0" })
            Assert.Throws<ArgumentException>(() => job.FailAttempt(attempt, "error", message, Now));
        AssertRunning(job, attempt);
    }

    [Fact]
    public void Completion_HasNoStandalonePublicAttemptMutationOrSetters()
    {
        Assert.DoesNotContain(typeof(JobAttempt).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            method => method.Name is "Succeed" or "Fail");
        foreach (var type in new[] { typeof(Job), typeof(JobAttempt), typeof(Lease) })
            Assert.DoesNotContain(type.GetProperties(), property => property.SetMethod?.IsPublic == true);
    }

    private static void AssertRunning(Job job, JobAttempt attempt)
    {
        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal(JobAttemptStatus.Running, attempt.Status);
        Assert.Null(job.CompletedAtUtc);
        Assert.Null(attempt.FinishedAtUtc);
        Assert.Null(attempt.Result);
        Assert.Null(attempt.ErrorCode);
        Assert.Null(attempt.ErrorMessage);
    }
    private static void Set(object entity, string property, object value) => entity.GetType().GetProperty(property)!.SetValue(entity, value);
    private static Job CreateJob() => new(Guid.CreateVersion7(), "test", "{}", 0, 1, Now, Now);
    private static Lease CreateLease() => new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Now, TimeSpan.FromSeconds(30));
}
