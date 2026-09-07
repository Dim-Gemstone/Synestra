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
