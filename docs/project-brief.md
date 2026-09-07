# Synestra Project Brief

## Purpose

Synestra is a control plane for executing background work across multiple
workers.

The system is responsible for:

- accepting units of work;
- tracking their lifecycle;
- assigning work to available workers;
- preventing unintended concurrent execution;
- detecting abandoned work;
- exposing stages, progress, partial results, and final results;
- supporting cooperative cancellation;
- eventually retrying failed or abandoned work under a separately defined policy;
- preserving execution history.

## Initial use case

The first concrete worker implementation is expected to execute browser-based
automation workloads.

The core scheduling/execution model should not depend on browser automation.

A Job can execute an entire scenario with stages, loops, and browser or API
operations. The worker-side handler owns those internal steps. See
`execution-scenarios.md` and ADR-0008 for the accepted scope.

Progress/results and cooperative cancellation are accepted requirements, not yet
implemented capabilities. Initial execution has no automatic retries. Pause is
deferred; live-process pause is a candidate and durable resume remains open.

## Actors

### Client

Submits jobs and queries their state.

Exact trust/authentication model is not yet defined.

### Worker

Coordinates jobs acquired through the Synestra worker protocol and supervises
their execution.

Workers:

- have a stable identity;
- advertise finite execution capacity;
- periodically report liveness;
- receive temporary leases rather than permanent ownership of work.

Workers communicate through the Worker API and do not access the control-plane
database directly.

The current working topology distinguishes a worker agent from the process that
executes a particular workload. A worker agent may coordinate with Synestra and
supervise one or more execution processes. ADR-0015 selects a long-lived,
capacity-one agent with in-process execution only for its built-in bounded test
workload. Unit 2E.1 implements the agent's identity and liveness, not execution.
General process isolation and other agent lifetime modes remain open.

### Synestra API / control plane

Coordinates job lifecycle and persists authoritative state.

The control plane has two logical API surfaces:

- Client API for the frontend and other product clients;
- Worker API for machine-to-machine execution coordination.

They may initially be hosted and deployed as one application while retaining
separate routes, contracts, and authorization boundaries.

## High-level topology

```text
Frontend / Product Client
           |
       Client API
           |
 Synestra Control Plane ---- PostgreSQL
           |
       Worker API
           |
      Worker Agent
           |
   Execution Process
```

PostgreSQL is internal to the control-plane boundary. ADR-0015 selects simple
Worker API polling for the minimal agent; unit 2E.1 does not implement claiming
yet. Other wakeup or delivery mechanisms remain open for later requirements.

## Core principles

- Job execution is asynchronous.
- Workers may disappear unexpectedly.
- Network communication may fail or be delayed.
- A worker must not permanently own a job.
- Execution history must remain observable.
- Coordination must remain correct with multiple concurrent workers.

## Non-goals for the initial version

Unless explicitly added later:

- general-purpose workflow/DAG orchestration;
- distributed transactions across arbitrary external systems;
- exactly-once execution guarantees;
- Kubernetes-style worker orchestration;
- arbitrary user-supplied executable code;
- large-scale cron scheduling.

## Open questions

- Delivery guarantee.
- Retry/backoff model.
- Cancellation semantics.
- Worker trust model.
- Explicit capacity release and execution resource accounting beyond ADR-0012 claims.
- Recurring jobs.
- Payload schema/versioning.
- Retention policy.
