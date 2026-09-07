using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Synestra.Api.IntegrationTests.Infrastructure;
using Synestra.Application.Executions;
using Synestra.Domain.Jobs;
using Synestra.Persistence;
using Xunit;

namespace Synestra.Api.IntegrationTests.Workers;

public sealed partial class ExecutionApiTests
{
    private WebApplicationFactory<Program> HostedFactory(FinalizerTestTimeProvider clock,
        PassProbe? probe = null, Dictionary<string, string>? settings = null, params IInterceptor[] interceptors) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:synestra", _database.ConnectionString);
            if (settings is not null)
                foreach (var (name, value) in settings) builder.UseSetting("ExecutionFinalization:" + name, value);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                if (interceptors.Length > 0)
                    services.AddDbContext<SynestraDbContext>(options => options.AddInterceptors(interceptors));
                if (probe is not null)
                {
                    var discovery = services.Single(service => service.ServiceType == typeof(IExpiredExecutionDiscovery));
                    services.Remove(discovery);
                    services.AddScoped<IExpiredExecutionDiscovery>(provider => new ProbedDiscovery(
                        (IExpiredExecutionDiscovery)ActivatorUtilities.CreateInstance(provider, discovery.ImplementationType!),
                        provider.GetRequiredService<SynestraDbContext>(), probe));
                    services.AddLogging(logging => logging.AddProvider(probe));
                }
            });
        });

    private async Task AssertHostLostAsync(Acquired execution, DateTimeOffset decision)
    {
        await using var context = Context();
        var job = await context.Jobs.Include(job => job.Attempts).ThenInclude(attempt => attempt.Lease)
            .SingleAsync(job => job.Id == execution.JobId, Token);
        var attempt = Assert.Single(job.Attempts);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(JobAttemptStatus.Abandoned, attempt.Status);
        Assert.Equal(decision.UtcDateTime, job.CompletedAtUtc);
        Assert.Equal(job.CompletedAtUtc, attempt.FinishedAtUtc);
        Assert.Equal(job.CompletedAtUtc, attempt.Lease!.ReleasedAtUtc);
        Assert.Equal("execution_lease_expired", attempt.ErrorCode);
        Assert.Null(context.Entry(attempt).Property<Guid?>("CompletionReportId").CurrentValue);
        Assert.Null(context.Entry(attempt).Property<string?>("CompletionSnapshot").CurrentValue);
    }

    private async Task AssertHostRunningAsync(Acquired execution)
    {
        await using var context = Context();
        Assert.Equal(JobStatus.Running, (await context.Jobs.SingleAsync(job => job.Id == execution.JobId, Token)).Status);
        Assert.Null((await context.Leases.SingleAsync(lease => lease.Id == execution.LeaseId, Token)).ReleasedAtUtc);
    }

    private async Task<string> HostExecutionRowsAsync()
    {
        await using var context = Context();
        return await context.Database.SqlQueryRaw<string>("""
            SELECT jsonb_agg(jsonb_build_object('lease', l, 'leaseVersion', l.xmin::text,
                'attempt', a, 'attemptVersion', a.xmin::text, 'job', j, 'jobVersion', j.xmin::text)
                ORDER BY l.id)::text AS "Value"
            FROM leases l JOIN job_attempts a ON a.id = l.job_attempt_id JOIN jobs j ON j.id = a.job_id
            """).SingleAsync(Token);
    }

    private sealed class PassProbe : ILoggerProvider
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public bool FailFirst { get; init; }
        public TaskCompletionSource? Gate { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<Guid> DisposedScopes { get; } = new();
        public ConcurrentQueue<(string Message, Exception? Exception)> Warnings { get; } = new();
        public ILogger CreateLogger(string categoryName) => new ProbeLogger(this, categoryName);
        public void Dispose() { }

        public async Task BeforeReturnAsync(CancellationToken token)
        {
            var call = Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            if (FailFirst && call == 1) throw new InvalidOperationException("private payload result token hash connection string");
            if (Gate is not null) await Gate.Task.WaitAsync(token);
        }

        private sealed class ProbeLogger(PassProbe probe, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (level >= LogLevel.Warning && category == "Synestra.Api.Executions.ExpiredExecutionFinalizer")
                    probe.Warnings.Enqueue((formatter(state, exception), exception));
            }
        }
    }

    private sealed class ProbedDiscovery(IExpiredExecutionDiscovery inner, SynestraDbContext context, PassProbe probe)
        : IExpiredExecutionDiscovery, IAsyncDisposable
    {
        public async Task<IReadOnlyList<ExpiredExecutionCursor>> FindAsync(DateTime cutoffUtc, ExpiredExecutionCursor? after,
            int limit, CancellationToken cancellationToken)
        {
            var candidates = await inner.FindAsync(cutoffUtc, after, limit, cancellationToken);
            await probe.BeforeReturnAsync(cancellationToken);
            return candidates;
        }
        public ValueTask DisposeAsync()
        {
            Assert.Null(context.Database.CurrentTransaction);
            Assert.Empty(context.ChangeTracker.Entries());
            probe.DisposedScopes.Enqueue(context.ContextId.InstanceId);
            return ValueTask.CompletedTask;
        }
    }
}
