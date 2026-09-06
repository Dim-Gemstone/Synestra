using System.Reflection;
using Synestra.Domain.Jobs;
using Synestra.Domain.Leases;
using Synestra.Domain.Workers;
using Xunit;

namespace Synestra.Domain.Tests.Jobs;

public sealed class ClaimTransitionTests
{
    private static readonly DateTime Now = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void StartAttempt_CreatesRunningOwnershipAndPreservesSubmission()
    {
        var job = CreateJob();
        var definition = job.JobDefinitionId;
        var attempt = job.StartAttempt(Now);
        var workerId = Guid.CreateVersion7();
        var sessionId = Guid.CreateVersion7();
        var lease = new Lease(attempt.Id, workerId, sessionId, Now, TimeSpan.FromSeconds(30));

        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Same(attempt, Assert.Single(job.Attempts));
        Assert.Equal(job.Id, attempt.JobId);
        Assert.Equal(1, attempt.Number);
        Assert.Equal(JobAttemptStatus.Running, attempt.Status);
        Assert.Equal(Now, attempt.StartedAtUtc);
        Assert.Equal(attempt.StartedAtUtc, lease.AcquiredAtUtc);
        Assert.Equal(Now.AddSeconds(30), lease.ExpiresAtUtc);
        Assert.Equal(attempt.Id, lease.JobAttemptId);
        Assert.Equal(workerId, lease.WorkerId);
        Assert.Equal(sessionId, lease.SessionId);
        Assert.All(new[] { job.Id, attempt.Id, lease.Id }, id =>
        {
            Assert.Equal(7, id.Version);
            Assert.Equal(0b1000, id.Variant & 0b1100);
        });
        Assert.Null(job.CompletedAtUtc);
        Assert.Null(attempt.FinishedAtUtc);
        Assert.Null(lease.ReleasedAtUtc);
        Assert.Equal("{\"value\":42}", job.Payload);
        Assert.Equal("test", job.Type);
        Assert.Equal(definition, job.JobDefinitionId);
        Assert.Equal(1, job.MaxAttempts);
        Assert.Equal(Now, job.CreatedAtUtc);
        Assert.Equal(Now, job.AvailableAtUtc);
        Assert.Throws<InvalidOperationException>(() => job.StartAttempt(Now));
        Assert.Single(job.Attempts);
    }

    [Theory]
    [InlineData(JobStatus.Running)]
    [InlineData(JobStatus.Succeeded)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    public void NonPendingJob_RejectsNewAttempt(JobStatus status)
    {
        var job = CreateJob();
        // Materialize otherwise unreachable persisted states without adding lifecycle APIs.
        typeof(Job).GetProperty(nameof(Job.Status))!.SetValue(job, status);
        Assert.Throws<InvalidOperationException>(() => job.StartAttempt(Now));
        Assert.Empty(job.Attempts);
        Assert.Equal(status, job.Status);
    }

    [Fact]
    public void InvalidStartTime_DoesNotPartlyMutateJob()
    {
        var job = CreateJob();
        Assert.Throws<ArgumentException>(() => job.StartAttempt(DateTime.SpecifyKind(Now, DateTimeKind.Unspecified)));
        Assert.Throws<ArgumentException>(() => job.StartAttempt(DateTime.SpecifyKind(Now, DateTimeKind.Local)));
        Assert.Throws<ArgumentOutOfRangeException>(() => job.StartAttempt(Now.AddTicks(-1)));
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Empty(job.Attempts);
    }

    [Fact]
    public void AttemptNumber_UsesMaximumHistoryAndDoesNotImplementRetryPolicy()
    {
        var job = CreateJob();
        var oldAttempt = job.StartAttempt(Now);
        typeof(JobAttempt).GetProperty(nameof(JobAttempt.Number))!.SetValue(oldAttempt, 5);
        typeof(Job).GetProperty(nameof(Job.Status))!.SetValue(job, JobStatus.Pending);
        Assert.Equal(6, job.StartAttempt(Now).Number);
        Assert.Equal(2, job.Attempts.Count);
        Assert.Equal(1, job.MaxAttempts);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("019ec569-5a00-4000-8000-000000000001")]
    [InlineData("019ec569-5a00-7000-0000-000000000001")]
    public void Lease_RejectsInvalidWorkerAndSessionIdentities(string value)
    {
        var invalid = Guid.Parse(value);
        Assert.Throws<ArgumentException>(() => new Lease(Guid.CreateVersion7(), invalid, Guid.CreateVersion7(), Now, TimeSpan.FromSeconds(30)));
        Assert.Throws<ArgumentException>(() => new Lease(Guid.CreateVersion7(), Guid.CreateVersion7(), invalid, Now, TimeSpan.FromSeconds(30)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Lease_RejectsNonPositiveDuration(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Lease(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Now, TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Lease_RejectsNonUtcAndMissingAttempt()
    {
        Assert.Throws<ArgumentException>(() => new Lease(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), DateTime.SpecifyKind(Now, DateTimeKind.Local), TimeSpan.FromSeconds(30)));
        Assert.Throws<ArgumentException>(() => new Lease(Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), Now, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Entities_ExposeNoPublicSettersOrMutableHistoryOrStandaloneAttemptCreation()
    {
        foreach (var type in new[] { typeof(Job), typeof(JobAttempt), typeof(Lease), typeof(Worker) })
        {
            Assert.DoesNotContain(type.GetProperties(BindingFlags.Public | BindingFlags.Instance), property => property.SetMethod?.IsPublic == true);
            Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance));
        }

        Assert.Empty(typeof(JobAttempt).GetConstructors());
        var job = CreateJob();
        var attempt = job.StartAttempt(Now);
        Assert.Throws<NotSupportedException>(() => ((ICollection<JobAttempt>)job.Attempts).Add(attempt));
    }

    private static Job CreateJob() => new(Guid.CreateVersion7(), "test", "{\"value\":42}", 0, 1, Now, Now);
}
