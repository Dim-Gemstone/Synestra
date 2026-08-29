# ADR-0003: Dedicated Database Migration Worker

**Status:** Accepted

## Context

Synestra is orchestrated locally using .NET Aspire and uses PostgreSQL through Entity Framework Core.

Database migrations must be applied before services that depend on the current database schema begin normal operation.

Running migrations directly as part of API startup would mix schema-management responsibilities with the long-running application process and make startup behavior harder to control.

Placing migration implementation directly inside AppHost would also give the orchestration project infrastructure responsibilities that belong closer to persistence.

## Decision

Synestra uses a dedicated one-shot migration worker to apply Entity Framework Core database migrations.

The migration worker:

1. starts as part of the distributed application;
2. obtains the configured database connection;
3. applies pending EF Core migrations;
4. exits after successful completion;
5. causes startup to fail visibly if migration execution fails.

Services that require the migrated schema may be configured by Aspire to wait for successful completion of this worker before starting or becoming operational.

## Responsibilities

The migration worker is responsible only for database initialization and migration-related startup work.

It is not a general-purpose background worker and must not contain Synestra job execution behavior.

The worker may reference the required Persistence infrastructure in order to create the appropriate `DbContext` and apply migrations.

## AppHost Responsibility

AppHost is responsible for orchestration:

- defining PostgreSQL resources;
- starting the migration worker;
- expressing startup dependencies between resources and projects.

AppHost should not itself contain EF Core migration implementation logic.

## API Responsibility

The API does not automatically apply migrations as part of its normal startup path.

This keeps API startup focused on serving the application and prevents multiple API replicas from independently attempting schema migration as a side effect of startup.

## Migration Generation

Migrations remain part of the Persistence project because they describe the database representation of the persistence model.

Generated migration files should normally remain generated artifacts.

Manual edits are acceptable only when required for intentional database behavior that EF migration generation cannot express correctly.

## Consequences

### Positive

- Database schema initialization has an explicit lifecycle.
- API startup remains separate from schema mutation.
- Aspire can model migration completion as an application dependency.
- The approach remains compatible with multiple long-running service instances.
- Migration failures are easier to identify as infrastructure startup failures.

### Trade-offs

- The solution contains an additional executable project.
- Startup orchestration must account for a one-shot process.
- Production deployment eventually needs equivalent migration ordering semantics.

## Future Deployment

The current implementation is primarily designed around Aspire-based development orchestration.

A future production/container deployment model must preserve the same essential property:

> schema migration completes successfully before incompatible application code begins normal operation.

The exact production deployment mechanism is deferred.

## Not Decided Here

This ADR does not define:

- production deployment tooling;
- rollback strategy;
- zero-downtime migration rules;
- backward-compatible migration policy;
- migration approval workflow;
- automatic production migration permissions.
