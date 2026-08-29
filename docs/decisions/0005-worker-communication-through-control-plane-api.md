# ADR-0005: Worker Communication Through the Control-Plane API

**Status:** Accepted

## Context

Synestra workers run independently from the control plane and may eventually
run on different machines or in different network environments.

Workers need to register, report liveness and capacity, receive work, maintain
execution ownership, and report execution results. Allowing them to access the
Synestra database directly would expose persistence details, distribute
database credentials, and couple worker deployment to the internal schema and
coordination queries.

Frontend and worker communication also serve different actors. Frontend-facing
operations express product use cases, while worker-facing operations form a
machine-to-machine execution coordination protocol.

## Decision

Workers communicate with the Synestra control plane through an API and do not
access the control-plane PostgreSQL database directly.

The control plane owns database access and performs concurrency-sensitive
coordination internally. PostgreSQL remains the authoritative store as defined
by ADR-0002.

Synestra exposes two logical API surfaces:

- **Client API** for the frontend and other product clients;
- **Worker API** for worker registration, coordination, and execution status.

These are logical security and contract boundaries. They may initially be
hosted by the same ASP.NET Core application and deployed as one service.
Splitting them into separate executables or deployments requires a concrete
operational reason and is not required by this decision.

## Client API Responsibilities

The Client API may expose use cases such as:

- managing job definitions;
- submitting jobs;
- querying jobs, attempts, and workers;
- requesting cancellation or retry when those semantics are defined;
- exposing execution history and operational state.

The Client API must not expose internal database coordination operations as
general CRUD endpoints.

## Worker API Responsibilities

The Worker API may expose protocol operations such as:

- registering or identifying a worker;
- reporting heartbeat and capacity;
- requesting available work;
- renewing execution ownership;
- reporting completion or failure;
- receiving cancellation information when cancellation semantics are defined.

The exact endpoints and protocol are not defined by this ADR.

## Security Boundary

Client and Worker APIs must be distinguishable in routing, contracts, and
authorization policy even while hosted in one process.

They may eventually use different:

- authentication mechanisms;
- authorization policies;
- network exposure;
- rate limits;
- API versions;
- OpenAPI documents.

The exact authentication and worker identity models remain undecided.

## Work Delivery

This decision does not require a specific delivery mechanism.

The initial implementation may allow a worker to request work while the
control plane performs an atomic PostgreSQL claim operation. A broker,
long-polling, streaming channel, or notification mechanism may be introduced
later if concrete requirements justify it.

Any delivery mechanism remains subordinate to authoritative control-plane
state and must not bypass lease and attempt validation.

## Consequences

### Positive

- Workers do not depend on the internal database schema.
- Database credentials remain inside the control-plane boundary.
- Coordination and lifecycle rules remain centralized.
- Worker protocol and persistence implementation can evolve independently.
- Frontend and worker operations can use different security policies.

### Trade-offs

- The control plane must provide and operate a machine-to-machine protocol.
- Work acquisition adds a network hop.
- API availability becomes part of worker coordination availability.
- Protocol versioning and compatibility must eventually be managed.

## Not Decided Here

This ADR does not define:

- exact Client API or Worker API endpoints;
- worker authentication or identity;
- polling, long-polling, streaming, push, or broker-based delivery;
- job-claim transaction semantics;
- lease renewal and expiration behavior;
- whether worker agents are long-lived or ephemeral;
- execution-process isolation;
- separate API deployment topology.
