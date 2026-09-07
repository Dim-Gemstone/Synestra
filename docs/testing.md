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
named-pipe transport; use the spelling above.

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

ADR-0015 units 2E.1 and 2E.2 add `src/Synestra.Worker`. It registers, heartbeats,
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
ownership is released. Normal shutdown exits 0; fatal failures exit 1. This unit
fails closed on transport errors without retry, report replay or re-registration.
Aspire wiring, definition bootstrap and full process qualification are later 2E units.

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
Result/error is persisted through the Worker API; the seven-field Client GET
response still exposes only its existing status and metadata.

Renewal uses the received lease duration and a monotonic deadline anchored before
claim with a five-second margin. Only acknowledged expiration increases extend
that deadline. Heartbeat proceeds independently; renewal and completion are
serialized. Lease loss stops local work, and finalization rejection cannot overwrite
the terminal state. Each HTTP operation, including its bounded response body,
has a five-second timeout. Recovery after an interrupted request remains 2E.3.

Run focused tests:

```powershell
dotnet test --project tests/Synestra.Worker.Tests
dotnet test --project tests/Synestra.Api.IntegrationTests --filter-method '*WorkerAgent*'
dotnet test --project tests/Synestra.Api.IntegrationTests --filter-class '*WorkerExecutionTests'
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
| API + PostgreSQL, finalizer disabled | Registration/liveness and session replacement; real bounded success/failure, persisted report/result/error and released lease; API restart between requests preserves session/ownership; Client GET retains seven fields |
| API + PostgreSQL, finalizer enabled | Stopped unfinished Worker execution is eventually abandoned; finalizer wins before renewal or a frozen completion and its terminal outcome is preserved |

The existing persistence/API suites retain concurrency, rollback and protocol
precedence coverage. Outage recovery and ambiguous-completion replay belong to
2E.3; separate-process and Aspire qualification belong to 2E.4.

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
