using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace Synestra.AppHost.Tests;

public sealed class WorkerProcessTests
{
    [Fact]
    public async Task LocalStackExecutesRenewsStopsAndRestartsRealWorkerProcesses()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        var directory = Path.Combine(Path.GetTempPath(), "synestra-process-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
        // DCP invokes the Go Docker CLI; the other test executables use .NET Testcontainers.
        // Normalize only this isolated test process, leaving the parent shell unchanged.
        if (OperatingSystem.IsWindows() && dockerHost?.StartsWith("npipe://./", StringComparison.Ordinal) == true)
            Environment.SetEnvironmentVariable("DOCKER_HOST", dockerHost.Replace("npipe://./", "npipe:////./", StringComparison.Ordinal));
        try
        {
            await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Synestra_AppHost>(
                [$"Worker:StateDirectory={directory}", "Dcp:WaitForResourceCleanup=true",
                    "Logging:LogLevel:Default=Warning"], token);
            // Keep the real startup graph, but never mount the developer's persistent database.
            var postgres = builder.Resources.OfType<PostgresServerResource>().Single();
            foreach (var mount in postgres.Annotations.OfType<ContainerMountAnnotation>().ToArray())
                postgres.Annotations.Remove(mount);
            builder.AddProject<Projects.Synestra_Worker>("duplicate-worker")
                .WithEnvironment("Worker__ApiBaseAddress", builder.CreateResourceBuilder<ProjectResource>("synestra-api").GetEndpoint("http"))
                .WithEnvironment("Worker__StateDirectory", directory)
                .WithExplicitStart();

            await using var app = await builder.BuildAsync(token);
            await app.StartAsync(token);
            await app.ResourceNotifications.WaitForResourceHealthyAsync("synestra-api", token);
            await using var database = new NpgsqlConnection(await app.GetConnectionStringAsync("synestra", token));
            await database.OpenAsync(token);
            using var client = app.CreateHttpClient("synestra-api", "http");
            var commands = app.Services.GetRequiredService<ResourceCommandService>();

            await EventuallyAsync(async () => await ScalarAsync<long>(database, "SELECT count(*) FROM workers", token) == 1, token);
            Assert.Equal(0, await ScalarAsync<long>(database, "SELECT count(*) FROM job_definitions", token));
            var workerId = await ScalarAsync<Guid>(database, "SELECT id FROM workers", token);
            Assert.Equal(workerId.ToString(), await File.ReadAllTextAsync(Path.Combine(directory, "worker-id"), token));
            var firstSession = await ScalarAsync<Guid>(database, "SELECT session_id FROM workers", token);
            var firstHeartbeat = await ScalarAsync<DateTime>(database, "SELECT last_seen_at_utc FROM workers", token);
            var workerEvent = await app.ResourceNotifications.WaitForResourceAsync("synestra-worker",
                resource => resource.Snapshot.State?.Text == KnownResourceStates.Running, token);
            Assert.DoesNotContain(workerEvent.Snapshot.EnvironmentVariables,
                variable => variable.Name.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(directory, Assert.Single(workerEvent.Snapshot.EnvironmentVariables,
                variable => variable.Name == "Worker__StateDirectory").Value);
            Assert.Equal(app.GetEndpoint("synestra-api", "http").GetLeftPart(UriPartial.Authority),
                Assert.Single(workerEvent.Snapshot.EnvironmentVariables, variable => variable.Name == "Worker__ApiBaseAddress").Value?.TrimEnd('/'));
            await StartAsync(commands, "duplicate-worker", token);
            await app.ResourceNotifications.WaitForResourceAsync("duplicate-worker", resource => resource.Snapshot.ExitCode == 1, token);
            Assert.Equal(firstSession, await ScalarAsync<Guid>(database, "SELECT session_id FROM workers", token));

            var preparation = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "prepare-bounded-workload.sql"), token);
            await ExecuteAsync(database, preparation, token);
            await ExecuteAsync(database, preparation, token);
            Assert.Equal(1, await ScalarAsync<long>(database, "SELECT count(*) FROM job_definitions", token));
            await ExecuteAsync(database, "UPDATE job_definitions SET is_enabled = FALSE", token);
            var disabled = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, preparation, token));
            Assert.Equal(PostgresErrorCodes.RaiseException, disabled.SqlState);
            Assert.False(await ScalarAsync<bool>(database, "SELECT is_enabled FROM job_definitions", token));
            await ExecuteAsync(database, "UPDATE job_definitions SET is_enabled = TRUE", token);

            var successfulJob = await SubmitAsync(client, 14000, false, token);
            await WaitForJobAsync(client, successfulJob, "running", token);
            await EventuallyAsync(async () => await ScalarAsync<bool>(database,
                "SELECT EXISTS (SELECT 1 FROM leases WHERE expires_at_utc > acquired_at_utc + interval '30 seconds')", token), token);
            Assert.True(await ScalarAsync<DateTime>(database, "SELECT last_seen_at_utc FROM workers", token) > firstHeartbeat);
            var succeeded = await AssertCompletionAsync(client, database, successfulJob, "succeeded", token);
            using (var result = JsonDocument.Parse(await ScalarAsync<string>(database,
                       "SELECT result::text FROM job_attempts WHERE status = 'Succeeded'", token)))
            {
                Assert.Equal(4, result.RootElement.GetProperty("count").GetInt32());
                Assert.Equal(3, result.RootElement.GetProperty("sum").GetInt64());
                Assert.Equal(2, result.RootElement.EnumerateObject().Count());
            }
            var failedJob = await SubmitAsync(client, 0, true, token);
            var failedInput = await AssertCompletionAsync(client, database, failedJob, "failed", token);
            Assert.Equal("invalid_workload_input", await ScalarAsync<string>(database,
                "SELECT error_code FROM job_attempts WHERE status = 'Failed'", token));
            Assert.Equal(2, await ScalarAsync<long>(database,
                "SELECT count(*) FROM job_attempts WHERE completion_report_id IS NOT NULL", token));
            Assert.Equal(0, await ScalarAsync<long>(database, "SELECT count(*) FROM leases WHERE released_at_utc IS NULL", token));

            await StopAsync(app, commands, "synestra-worker", token);
            using (File.Open(Path.Combine(directory, "worker.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            await StopAsync(app, commands, "synestra-api", token);
            await StartAsync(commands, "synestra-api", token);
            await app.ResourceNotifications.WaitForResourceHealthyAsync("synestra-api", token);
            await StartAsync(commands, "synestra-worker", token);
            await EventuallyAsync(async () => await ScalarAsync<Guid>(database, "SELECT session_id FROM workers", token) != firstSession, token);
            Assert.Equal(workerId, await ScalarAsync<Guid>(database, "SELECT id FROM workers", token));
            Assert.True(JsonElement.DeepEquals(succeeded, await ReadJobAsync(client, successfulJob, token)));
            Assert.True(JsonElement.DeepEquals(failedInput, await ReadJobAsync(client, failedJob, token)));

            var interruptedJob = await SubmitAsync(client, 60000, false, token);
            await WaitForJobAsync(client, interruptedJob, "running", token);
            await StopAsync(app, commands, "synestra-worker", token);
            Assert.Equal("running", (await ReadJobAsync(client, interruptedJob, token)).GetProperty("status").GetString());
            // Only the harness advances persisted expiration after the actual process has exited.
            // Protocol timing/deadline behavior is covered separately using controlled time.
            await ExecuteAsync(database, "UPDATE leases SET expires_at_utc = acquired_at_utc + interval '1 millisecond' WHERE released_at_utc IS NULL", token);
            var interrupted = await AssertCompletionAsync(client, database, interruptedJob, "abandoned", token);
            Assert.Equal(1, await ScalarAsync<long>(database,
                "SELECT count(*) FROM job_attempts WHERE status = 'Abandoned' AND error_code = 'execution_lease_expired' AND completion_report_id IS NULL AND result IS NULL", token));

            await StartAsync(commands, "synestra-worker", token);
            var crashedJob = await SubmitAsync(client, 60000, false, token);
            await WaitForJobAsync(client, crashedJob, "running", token);
            var crashedSession = await ScalarAsync<Guid>(database, "SELECT session_id FROM workers", token);
            var running = await app.ResourceNotifications.WaitForResourceAsync("synestra-worker",
                resource => resource.Snapshot.State?.Text == KnownResourceStates.Running && resource.Snapshot.ExitCode is null, token);
            var processId = Convert.ToInt32(Assert.Single(running.Snapshot.Properties,
                property => property.Name == "executable.pid").Value, CultureInfo.InvariantCulture);
            // Kill only the exact process owned by this isolated Aspire resource.
            using (var process = Process.GetProcessById(processId))
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(token);
            }
            await app.ResourceNotifications.WaitForResourceAsync("synestra-worker", resource => resource.Snapshot.ExitCode.HasValue, token);
            Assert.Equal("running", (await ReadJobAsync(client, crashedJob, token)).GetProperty("status").GetString());
            await StartAsync(commands, "synestra-worker", token);
            await EventuallyAsync(async () => await ScalarAsync<Guid>(database, "SELECT session_id FROM workers", token) != crashedSession, token);
            Assert.Equal("running", (await ReadJobAsync(client, crashedJob, token)).GetProperty("status").GetString());
            Assert.Equal(1, await ScalarAsync<long>(database, "SELECT count(*) FROM job_attempts WHERE status = 'Running'", token));
            await ExecuteAsync(database, "UPDATE leases SET expires_at_utc = acquired_at_utc + interval '1 millisecond' WHERE released_at_utc IS NULL", token);
            var crashed = await AssertCompletionAsync(client, database, crashedJob, "abandoned", token);
            var nextJob = await SubmitAsync(client, 0, false, token);
            await AssertCompletionAsync(client, database, nextJob, "succeeded", token);
            Assert.Equal(2, await ScalarAsync<long>(database, "SELECT count(*) FROM job_attempts WHERE status = 'Abandoned' AND completion_report_id IS NULL", token));
            await StopAsync(app, commands, "synestra-worker", token);
            await StopAsync(app, commands, "synestra-api", token);
            await StartAsync(commands, "synestra-api", token);
            await app.ResourceNotifications.WaitForResourceHealthyAsync("synestra-api", token);
            Assert.True(JsonElement.DeepEquals(interrupted, await ReadJobAsync(client, interruptedJob, token)));
            Assert.True(JsonElement.DeepEquals(crashed, await ReadJobAsync(client, crashedJob, token)));

            // The real executable must fail startup without replacing a corrupt persisted identity.
            await File.WriteAllTextAsync(Path.Combine(directory, "worker-id"), "corrupt", token);
            await StartAsync(commands, "synestra-worker", token);
            var failed = await app.ResourceNotifications.WaitForResourceAsync("synestra-worker",
                resource => resource.Snapshot.ExitCode == 1, token);
            Assert.Equal(1, failed.Snapshot.ExitCode);
            Assert.Equal("corrupt", await File.ReadAllTextAsync(Path.Combine(directory, "worker-id"), token));
            Assert.Equal(5, await ScalarAsync<long>(database, "SELECT count(*) FROM job_attempts", token));
            Assert.Equal(workerId, await ScalarAsync<Guid>(database, "SELECT id FROM workers", token));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOCKER_HOST", dockerHost);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task StopAsync(DistributedApplication app, ResourceCommandService commands, string resource, CancellationToken token)
    {
        var result = await commands.ExecuteCommandAsync(resource, KnownResourceCommands.StopCommand, token);
        Assert.True(result.Success, result.Message);
        var stopped = await app.ResourceNotifications.WaitForResourceAsync(resource,
            snapshot => snapshot.Snapshot.ExitCode.HasValue, token);
        Assert.Equal(0, stopped.Snapshot.ExitCode);
    }

    private static async Task StartAsync(ResourceCommandService commands, string resource, CancellationToken token)
    {
        var result = await commands.ExecuteCommandAsync(resource, KnownResourceCommands.StartCommand, token);
        Assert.True(result.Success, result.Message);
    }

    private static async Task<Guid> SubmitAsync(HttpClient client, int duration, bool invalid, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/client/jobs")
        {
            Content = JsonContent.Create(new
            {
                type = "test.bounded-sum.v1",
                payload = new { values = invalid ? Array.Empty<int>() : [1, 2, -1000000, 1000000], durationMs = duration }
            })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await client.SendAsync(request, token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = await response.Content.ReadFromJsonAsync<JsonDocument>(token);
        return body!.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> ReadJobAsync(HttpClient client, Guid id, CancellationToken token)
    {
        using var response = await client.GetAsync($"/api/client/jobs/{id}", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var view = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.Equal(new[] { "availableAtUtc", "completedAtUtc", "completion", "createdAtUtc", "id", "maxAttempts", "priority", "status", "type" },
            view.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(id, view.GetProperty("id").GetGuid());
        if (view.GetProperty("status").GetString() is "pending" or "running")
        {
            Assert.Equal(JsonValueKind.Null, view.GetProperty("completedAtUtc").ValueKind);
            Assert.Equal(JsonValueKind.Null, view.GetProperty("completion").ValueKind);
        }
        return view;
    }

    private static async Task<JsonElement> WaitForJobAsync(HttpClient client, Guid id, string status, CancellationToken token)
    {
        JsonElement view = default;
        await EventuallyAsync(async () =>
        {
            view = await ReadJobAsync(client, id, token);
            return view.GetProperty("status").GetString() == status;
        }, token);
        return view;
    }

    private static async Task<JsonElement> AssertCompletionAsync(HttpClient client, NpgsqlConnection database, Guid id, string outcome, CancellationToken token)
    {
        var view = await WaitForJobAsync(client, id, outcome == "abandoned" ? "failed" : outcome, token);
        var completion = view.GetProperty("completion");
        Assert.Equal(new[] { "attemptId", "attemptNumber", "error", "outcome", "result" },
            completion.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(outcome, completion.GetProperty("outcome").GetString());
        Assert.EndsWith("Z", view.GetProperty("completedAtUtc").GetString());
        if (outcome == "succeeded")
        {
            Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>("""{"count":4,"sum":3}"""), completion.GetProperty("result")));
            Assert.Equal(JsonValueKind.Null, completion.GetProperty("error").ValueKind);
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, completion.GetProperty("result").ValueKind);
            var error = completion.GetProperty("error");
            Assert.Equal(new[] { "code", "message" }, error.EnumerateObject().Select(property => property.Name).Order());
            Assert.Equal(outcome == "abandoned" ? "execution_lease_expired" : "invalid_workload_input", error.GetProperty("code").GetString());
            Assert.Equal(outcome == "abandoned" ? "Execution lease expired before completion was recorded." : "The bounded workload input is invalid.",
                error.GetProperty("message").GetString());
        }

        await using var command = new NpgsqlCommand("""
            SELECT a.id, a.number, j.completed_at_utc, a.finished_at_utc, l.released_at_utc
            FROM jobs j JOIN job_attempts a ON a.job_id = j.id JOIN leases l ON l.job_attempt_id = a.id
            WHERE j.id = @id
            """, database);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        Assert.Equal(reader.GetGuid(0), completion.GetProperty("attemptId").GetGuid());
        Assert.Equal(reader.GetInt32(1), completion.GetProperty("attemptNumber").GetInt32());
        for (var column = 2; column <= 4; column++)
            Assert.Equal(reader.GetDateTime(column), view.GetProperty("completedAtUtc").GetDateTime());
        Assert.False(await reader.ReadAsync(token));
        return view;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection database, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, database);
        return (T)(await command.ExecuteScalarAsync(token))!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection database, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, database);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task EventuallyAsync(Func<Task<bool>> predicate, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!await predicate()) await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
    }
}
