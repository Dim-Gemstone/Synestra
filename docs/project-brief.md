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
- retrying failed or abandoned work according to policy;
- preserving execution history.

## Initial use case

The first concrete worker implementation is expected to execute browser-based
automation workloads.

The core scheduling/execution model should not depend on browser automation.

## Actors

### Client

Submits jobs and queries their state.

Exact trust/authentication model is not yet defined.

### Worker

Executes jobs assigned through the Synestra coordination protocol.

Workers:

- have a stable identity;
- advertise finite execution capacity;
- periodically report liveness;
- receive temporary leases rather than permanent ownership of work.

### Synestra API / control plane

Coordinates job lifecycle and persists authoritative state.

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
- Exact capacity accounting.
- Recurring jobs.
- Payload schema/versioning.
- Retention policy.
