# Testing

Synestra uses xUnit.net v3 on Microsoft Testing Platform (MTP). The package is
named `xunit.v3`; its package version may have a higher major version than the
framework generation. Package versions are managed centrally in
`Directory.Packages.props`, and the .NET 10 SDK plus MTP runner are selected by
`global.json`.

Run the complete suite from the repository root:

```powershell
dotnet restore
dotnet test --solution Synestra.slnx
```

Verify that Solution Explorer mirrors every Markdown file under `docs/`, every
file under `scripts/`, and contains no stale references:

```powershell
./scripts/verify-solution-items.ps1
```

CI runs this check before restore and tests.

## PostgreSQL integration tests

Integration tests use Testcontainers and require:

- .NET SDK version selected by `global.json`;
- a running Docker-API-compatible Linux container runtime;
- permission to connect to the runtime and pull images from Docker Hub;
- enough capacity to run `postgres:17.11-alpine3.24`.

Docker Desktop is the supported local option on Windows and macOS. Docker
Engine is the supported CI option on Linux. Alternative Docker-compatible
runtimes may work but are not part of the supported test matrix.

On Windows, Docker Desktop may expose its active Linux engine through
`dockerDesktopLinuxEngine` while Testcontainers probes the unavailable
`docker_engine` pipe. If `docker context ls` confirms that active endpoint, set
the .NET-compatible URI for the current test shell:

```powershell
$env:DOCKER_HOST = 'npipe://./pipe/dockerDesktopLinuxEngine'
dotnet test --solution Synestra.slnx
```

This is a local environment override, not a fixture or CI configuration change.
The Docker CLI's four-slash URI spelling is not accepted by the current .NET
named-pipe transport; use the spelling above for the full suite. The isolated
AppHost test executable normalizes that URI for its Docker CLI child processes;
it does not change the parent shell or the Testcontainers test executables.
For a manual Docker CLI or AppHost command, use `npipe:////./pipe/dockerDesktopLinuxEngine`
if an explicit endpoint override is needed.

The PostgreSQL image includes exact PostgreSQL and Alpine versions so upstream
floating tags cannot silently change test behavior. Update the identical tag in
the integration fixture, AppHost, CI audit, and this document together.

All tests that use the shared PostgreSQL container belong to the
`PostgreSQL integration tests` collection. xUnit can run unrelated collections
in parallel, while database tests in this collection run serially. The fixture
shares one container for startup efficiency, but creates a fresh database for
every test and drops it afterward. Integration tests must use that per-test
database and must never depend on execution order or data left by another test.

Cross-run Testcontainers reuse is deliberately disabled because the feature is
experimental and can retain state or resources. If per-test database creation
becomes a measurable bottleneck, replace it only with another verified isolation
mechanism, such as a fresh schema or deterministic database reset. Do not trade
test isolation for execution speed implicitly.

## Automatic execution finalization

The API host runs the ADR-0014 finalizer by default. Configure it through the
`ExecutionFinalization` section (or equivalent environment variables):

```json
{
  "ExecutionFinalization": {
    "Enabled": true,
    "IntervalSeconds": 5,
    "BatchSize": 100
  }
}
```

These are the production defaults. The first pass starts after ApplicationStarted
without an interval delay; each later delay starts after the preceding pass and
scope disposal. Startup validates positive batch size and an interval of 1 through
4,294,967 seconds (the runtime timer bound), including when disabled. Configuration
changes require host restart. Set `ExecutionFinalization__Enabled=false` to disable
the finalizer explicitly; eventual finalization is not promised in that mode.
Database migrations still run separately, never during API startup.

API factories that test submission or Worker protocol behavior explicitly set
`ExecutionFinalization:Enabled` to `false`. This includes factories derived for
concurrency and restart tests. They must not rely on the test clock incidentally
preventing expiration. Host finalization tests use a separate factory with defaults
enabled (or an explicit setting when testing configuration).

`FinalizationHostTests` exercises real PostgreSQL through the service host. Its
TimeProvider supplies both UTC decision time and controlled one-shot delay timers.
Tests await timer registration after scope disposal before advancing time; SQL
commit and discovery gates coordinate races without long real sleeps. Each pass
uses a fresh scope, while only the traversal cursor crosses passes. Shutdown tests
cancel both a pending delay and an active transaction; restart rediscovers the
rolled-back execution. Unrelated low-level concurrency matrices remain in the
Domain, Application, Persistence and Worker API suites.

Run the host group with the MTP filter:

```powershell
dotnet test --project tests/Synestra.Api.IntegrationTests --filter-method '*HostFinalization*'
```

## Minimal Worker and bounded execution

ADR-0015 units 2E.1 through 2E.3 add `src/Synestra.Worker`. It registers, heartbeats,
claims one Job, executes `test.bounded-sum.v1`, renews ownership and reports completion.
Start it against an already running API in the trusted/private development boundary:

```powershell
dotnet run --project src/Synestra.Worker -- --Worker:ApiBaseAddress https://localhost:PORT --Worker:StateDirectory C:\synestra-worker-state --Worker:Name worker-01
```

Replace the origin with the actual API endpoint and choose a writable absolute
state directory outside the repository. On Linux, use an absolute path such as
`/tmp/synestra-worker-state` for a disposable test identity. Preserve the directory
across restarts when retaining Worker identity. The same options can be supplied
as `Worker__ApiBaseAddress`, `Worker__StateDirectory`, and `Worker__Name` environment
variables. HTTPS uses normal certificate validation; HTTP is supported for a
trusted local test endpoint. Worker does not need PostgreSQL credentials.

The Worker creates `worker-id` once, flushes it before registration, and holds
`worker.lock` exclusively until exit. Do not copy a live identity or share its
state directory between processes. Corrupt identity fails without regeneration.
Stop with Ctrl+C/SIGTERM; new claims stop and unfinished execution is cancelled
locally without a synthetic outcome. An already frozen report may finish delivery
within the ten-second shutdown budget. Owned tasks/timers are drained and identity
ownership is released. Normal shutdown exits 0; fatal failures exit 1. Registration,
renewal and frozen completion support bounded transport recovery; exhausting a
pending completion's shutdown budget also exits 1.
Local Aspire orchestration and process qualification are covered below.

The test harness explicitly prepares the `test.bounded-sum.v1` JobDefinition;
Worker never seeds definitions or migrates storage. Submit a Job using the existing
Client API and this payload after preparing that definition:

```json
{"type":"test.bounded-sum.v1","payload":{"values":[1,2,-3,4],"durationMs":40000}}
```

The payload has exactly two fields: 1–1024 integer values in [-1000000,1000000]
and integer durationMs in [0,60000]. Equivalent JSON integer spellings are accepted;
fractions, numeric underflow/rounding, extra fields and invalid bounds are rejected.
The handler awaits through TimeProvider and produces exactly `{"count":4,"sum":4}`
for this example. Invalid payload produces a fixed `invalid_workload_input` failure.
Execution uses bounded memory and a 65-second watchdog, with no external I/O.
Result/error is persisted through the Worker API. ADR-0016 Client GET now adds
completedAtUtc and completion to its original seven fields (unit 2F.1).

Renewal uses the received lease duration and a monotonic deadline anchored before
claim with a five-second margin. Only acknowledged expiration increases extend
that deadline. Heartbeat proceeds independently; renewal and completion are
serialized. Lease loss stops local work, and finalization rejection cannot overwrite
the terminal state. Each HTTP operation, including its bounded response body,
has a five-second timeout. Unit 2E.3 retries only registration, renewal and frozen
completion: at most three total attempts, one-second waits and a fifteen-second
monotonic budget, also constrained by lease/watchdog/shutdown deadlines. Each
attempt owns a fresh request. Registration keeps the same identity/session and
desired state; completion reuses identical serialized report bytes and credentials.

Recovery handles connection failures, interrupted streams, timeouts and the
existing `500 internal_error` response. Other statuses/codes, malformed success
responses, size violations and domain conflicts are not repeated. Claim and
heartbeat are single-attempt operations. A committed claim with a lost response
cannot recover its token and is left to finalization. No pending report or session
is persisted locally, and restarting Worker cannot adopt its old execution.

Run focused tests:

```powershell
dotnet test --project tests/Synestra.Worker.Tests
dotnet test --project tests/Synestra.Api.IntegrationTests --filter-method '*WorkerAgent*'
dotnet test --project tests/Synestra.Api.IntegrationTests --filter-class '*WorkerExecutionTests'
dotnet test --project tests/Synestra.Api.IntegrationTests --filter-method '*Recovery*'
```

The controlled TimeProvider supplies UTC, monotonic timestamps and observable
one-shot timers. Tests observe all relevant timers/HTTP gates before advancing
time, including multiple timers with the same remaining duration. No long real
sleeps are used. API tests run the real Worker host over TestServer HTTP against
a fresh migrated PostgreSQL database. The test harness alone accesses PostgreSQL;
Worker has no server project reference.

| Layer | Verified behavior |
| --- | --- |
| Worker unit/host | Identity, configuration, safe diagnostics, exact HTTP contracts, input/resource bounds, deterministic output, 40-second virtual execution with independent heartbeat/renewal, no prefetch, delayed claim, monotonic cutoff, fencing, fatal ambiguous requests, finalization conflicts, active shutdown and frozen-report drain |
| API + PostgreSQL, finalizer disabled | Registration/liveness and session replacement; real bounded success/failure, persisted report/result/error and released lease; API restart between requests preserves session/ownership; GET uses the ADR-0016 field set |
| API + PostgreSQL, finalizer enabled | Stopped unfinished Worker execution is eventually abandoned; finalizer wins before renewal or a frozen completion and its terminal outcome is preserved |
| Worker recovery tests | Fresh requests with identical bodies/headers, three-attempt/15-second cap including waits, UTC regression, caller cancellation, acknowledged renewal recovery, completion replay without prefetch/renewal, definitive conflicts, successful shutdown replay and fatal shutdown exhaustion |
| API + PostgreSQL recovery tests | Registration response lost after commit preserves identity/history and cannot displace replacement; committed success/failure replay across API restart and lease expiry preserves exact response and execution rows; unacknowledged renewal does not extend the local budget; ambiguous claim stops and Worker restart cannot adopt it; enabled-host finalization records unreported loss |
| Aspire + real executable processes + PostgreSQL | Migration/API readiness ordering, explicit definition preparation, actual heartbeat/renewal/completion, graceful shutdown, identity lock and corrupt startup, crash/restart without adoption, API restart, loss finalization and subsequent work |

The existing persistence/API suites retain concurrency, rollback and protocol
precedence coverage.

## Local stack and separate-process qualification (2E.4)

AppHost uses the SDK's version-matched NuGet dashboard/orchestration tools.
Restore downloads the tools for the host platform; no separately installed
Aspire CLI bundle is required for local launch or CI process tests.
AppHost narrowly suppresses ASPIRE010 for this deliberate NuGet-tooling choice;
the current graph uses no bundle-only features. Revisit it when adding such features.

Start the local stack with the .NET SDK and Docker running:

```powershell
dotnet run --project orchestration/Synestra.AppHost --launch-profile http
```

Aspire starts PostgreSQL, waits for MigrationWorker to finish, waits for the API's
`/health` HTTP check, and then starts Worker. The API uses its explicit `http`
launch profile so its configured Worker origin does not redirect to HTTPS.
`/health` checks HTTP host availability, not database health; initial schema
readiness is provided by the migration dependency. This remains trusted local
development orchestration, not a production deployment policy.

AppHost supplies Worker with the API origin and an absolute state directory under
the current user's LocalApplicationData folder (`Synestra/worker`). Override it
for another local identity or concurrent checkout:

```powershell
dotnet run --project orchestration/Synestra.AppHost --launch-profile http -- --Worker:StateDirectory C:\synestra-worker-state
```

On Linux use an absolute writable path instead. Preserve this directory for
stable identity across AppHost restarts. Worker receives no database reference
or connection string. Only MigrationWorker applies migrations; neither API nor
Worker seeds JobDefinitions. A new database initially has no executable definitions.

After migrations complete, explicitly prepare the single development definition.
Copy the exact PostgreSQL container name from the Aspire dashboard or `docker ps`:

```powershell
$postgresContainer = 'REPLACE_WITH_LOCAL_ASPIRE_POSTGRES_CONTAINER_NAME'
Get-Content -Raw scripts/prepare-bounded-workload.sql | docker exec -i $postgresContainer psql -U postgres -d synestra -v ON_ERROR_STOP=1
```

On a POSIX shell the equivalent is
`docker exec -i CONTAINER psql -U postgres -d synestra -v ON_ERROR_STOP=1 < scripts/prepare-bounded-workload.sql`.
This explicit dev/test script is idempotent by type. It preserves an existing
definition and fails if that definition is disabled, rather than enabling it.
Its fixed UUID v7 is only the development definition's identity. It is not a
definition-management API or an automatic startup seed.

Use the API's HTTP URL shown in the dashboard to submit and observe a Job:

```powershell
$apiOrigin = 'http://localhost:REPLACE_WITH_API_PORT'
$job = Invoke-RestMethod -Method Post -Uri "$apiOrigin/api/client/jobs" -ContentType 'application/json' -Headers @{ 'Idempotency-Key' = [guid]::NewGuid().ToString() } -Body '{"type":"test.bounded-sum.v1","payload":{"values":[1,2,-3,4],"durationMs":14000}}'
Invoke-RestMethod -Uri "$apiOrigin/api/client/jobs/$($job.id)"
```

Repeat GET to observe terminal status and completion.result or completion.error.
Pending/Running have null completion. The 14-second example exercises renewal
with the current 30-second lease and independent 10-second heartbeat.

Run the separate-process qualification with:

```powershell
dotnet test --project tests/Synestra.AppHost.Tests
```

This xUnit/MTP project uses Aspire.Hosting.Testing with the actual AppHost graph,
real executable entry points, loopback HTTP and the pinned PostgreSQL container.
Each test gets a fresh container without the development data volume and a unique
temporary identity directory. Disposal awaits Aspire resource termination before
removing test state. The harness alone uses Npgsql and the explicit preparation
script; it does not give Worker database credentials or add a test control API.
Tests run in the normal solution/CI suite with no interactive dashboard required.

The process scenario verifies startup ordering, no automatic definition seed,
deterministic success and input failure, actual heartbeat/renewal, persisted
completion and lease release, the current Client GET field set, graceful idle/active
shutdown, exclusive identity ownership across two processes, crash/restart with
no adoption of the old attempt, stable identity with a fresh session, persisted
state across API restart, server finalization and fatal corrupt-identity startup.
Bounded polling observes progress; one 14-second workload exercises real process
timers. After stopping an unfinished process, the harness moves only its persisted
lease expiration into the past to qualify the enabled finalizer without a long
real-time expiry wait. Exact deadlines, shutdown report drain, response loss and
conflict races remain in controlled-time Worker/API/PostgreSQL tests above.

## Client terminal reads (2F.1)

GET now returns nine top-level fields: the original submission metadata plus
completedAtUtc and completion. An associated terminal completion identifies its
attempt and contains outcome, result and error, with explicit nulls. Loss is
Job failed / completion abandoned, with execution_lease_expired. A failed Job
is a successful HTTP read (200), not a transport error. Result is embedded JSON.

Read tests cover real Worker reports and internal finalization, UTC, exact field
sets, API restart, original POST replay and opaque JSON (64 KiB received result,
depth 32, Unicode and jsonb numeric limits). PostgreSQL tests check greatest
attempt number before association, legacy reportless/partial rows, UTF-8 legacy
result bounds before transfer, one statement without tracking/row locks, and
committed visibility while a terminal writer holds locks. Expired Running work
is not finalized by GET. Legacy missing/oversized result is null, not fabricated
or truncated output; bounded legacy nesting remains readable.

Focused commands (the full solution suite is still required):

```powershell
dotnet test --project tests/Synestra.Application.Tests --filter-class '*GetJobTests'
dotnet test --project tests/Synestra.Persistence.IntegrationTests --filter-method '*ClientRead*'
dotnet test --project tests/Synestra.Api.IntegrationTests --filter-method '*ClientRead*'
```

Existing completion/finalization API tests also check the new read representation.
Unit 2F.2 remains responsible for qualifying the complete Client submit-to-result
path with actual Worker execution, response loss/replay and finalization races,
and extending separate-process qualification with Client result/loss observations.
The field-set adaptations in existing process tests do not complete that unit.

## Dependency and license audit

CI performs three checks:

- NuGet vulnerability and deprecation reports, including transitive packages;
- Trivy filesystem scanning for package vulnerabilities and detected licenses;
- Trivy scanning of the pinned PostgreSQL image for vulnerabilities and
  detected licenses.

License findings are audit output for review. High and critical known
vulnerabilities in repository dependencies fail CI. Findings in the upstream
PostgreSQL development/test image remain visible but do not fail CI because the
image is not a Synestra production artifact and cannot be remediated in this
repository. Unfixed image findings are omitted to keep that report actionable.

If Synestra later distributes or deploys a PostgreSQL image as a production
artifact, introduce an explicit container policy with digest pinning, reviewed
VEX or narrowly scoped exceptions, and a remediation window before turning the
image scan into a merge gate. Any future license allow/deny policy must likewise
be recorded explicitly before making license names a build gate.
