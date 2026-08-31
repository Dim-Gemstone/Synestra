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

All tests that use the shared database container belong to the
`PostgreSQL integration tests` collection. xUnit can run unrelated collections
in parallel, while database tests in this collection run serially. The fixture
shares one container within a test run and disposes it afterward. Cross-run
Testcontainers reuse is deliberately disabled because the feature is
experimental and can retain state or resources.

Each integration test must isolate the database state it creates. When the
suite grows beyond the current single test, prefer a fresh database/schema or a
verified reset between tests rather than relying on test order.

## Dependency and license audit

CI performs three checks:

- NuGet vulnerability and deprecation reports, including transitive packages;
- Trivy filesystem scanning for package vulnerabilities and detected licenses;
- Trivy scanning of the pinned PostgreSQL image for vulnerabilities and
  detected licenses.

License findings are audit output for review. High and critical known
vulnerabilities fail CI. Any future license allow/deny policy must be recorded
explicitly before making license names a build gate.
