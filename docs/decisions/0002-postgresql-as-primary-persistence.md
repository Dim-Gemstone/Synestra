# ADR-0002: PostgreSQL as Primary Persistence and Coordination Store

**Status:** Accepted

## Context

Synestra coordinates asynchronous work across independently running workers.

The system must persist authoritative information about:

- job definitions;
- jobs;
- execution attempts;
- leases;
- workers;
- worker liveness and capacity-related state.

Workers may run concurrently, disappear unexpectedly, or race to acquire available work.

The system therefore requires a durable store capable of supporting both ordinary persistence and concurrency-sensitive coordination.

Introducing a separate message broker or distributed coordination system at the current stage would increase operational and architectural complexity before the required execution semantics are fully understood.

## Decision

PostgreSQL is the primary authoritative durable store for Synestra.

Entity Framework Core is used for general persistence, schema mapping, and migrations.

PostgreSQL will also be used for coordination operations where database-level atomicity and concurrency control are appropriate.

Examples include potentially:

- atomic job claiming;
- lease acquisition;
- lease state validation;
- protection against conflicting concurrent updates;
- recovery of abandoned work.

Coordination that must remain correct across processes or machines must not rely on in-memory synchronization primitives.

## Source of Truth

Persisted PostgreSQL state is authoritative for control-plane state.

In-memory state may be used for:

- caching;
- local optimization;
- transient computation.

It must not be the sole source of truth for distributed execution ownership or job lifecycle state.

## Concurrency

Concurrency-sensitive operations must be designed explicitly.

The implementation may use PostgreSQL mechanisms such as:

- transactions;
- row-level locks;
- conditional updates;
- uniqueness constraints;
- optimistic concurrency;
- `FOR UPDATE`;
- `SKIP LOCKED`.

This ADR does not select the exact mechanism for individual operations.

In particular, the final job-claiming algorithm requires a separate decision.

## Database Constraints

Rules that must hold globally across concurrent processes should preferably be protected by the database when practical.

Examples may include:

- uniqueness;
- relationship integrity;
- prevention of impossible duplicate records;
- other invariants that cannot safely rely only on application-process checks.

Domain validation and database constraints are complementary rather than interchangeable.

## Entity Framework Core

EF Core is the default persistence technology for ordinary domain persistence.

EF configuration remains outside domain entities.

PostgreSQL-specific or concurrency-critical operations may use lower-level SQL where EF-generated queries are insufficient or obscure required database semantics.

Introducing direct SQL for such operations is acceptable when:

- the concurrency behavior is explicit;
- the query is covered by integration tests;
- the database-specific behavior is intentional.

## Consequences

### Positive

- Durable state and coordination share one transactional system.
- Initial operational complexity remains low.
- PostgreSQL provides mature concurrency primitives.
- Execution history and coordination state can be reasoned about together.
- The architecture avoids adding a broker before there is a demonstrated need.

### Trade-offs

- Some coordination logic becomes PostgreSQL-specific.
- High-throughput scheduling may eventually expose database contention.
- Database queries used for claiming and recovery require careful concurrency testing.
- Scaling characteristics must be measured rather than assumed.

## Future Evolution

A separate message broker, event stream, cache, or coordination component may be introduced later if concrete scale, latency, or isolation requirements justify it.

Such components must not silently replace PostgreSQL as the authoritative control-plane state without a separate architectural decision.

## Not Decided Here

This ADR does not define:

- the exact atomic job-claim algorithm;
- delivery guarantees;
- lease expiration behavior;
- retry semantics;
- retention strategy;
- expected throughput limits;
- whether a broker will ever be introduced.
