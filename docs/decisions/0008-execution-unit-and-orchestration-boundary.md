# ADR-0008: Execution Unit and Orchestration Boundary

**Status:** Accepted

## Context

The reference workloads include downloading or parsing a file, setting up an
account, and sending a sequence of letters. A single request may involve stages,
loops, authentication, browser contexts, and external APIs. These requirements
need a clearer execution boundary before defining the Worker API.

See [execution scenarios](../execution-scenarios.md). This decision accepts
product semantics; it does not claim that execution is already implemented.

## Decision

### A Job can execute a complete scenario

A Job represents one requested logical unit of work. Its worker-side handler may
implement multiple stages, loops, and browser or API operations. These internal
steps do not automatically become separate Jobs or JobAttempts. A JobAttempt
represents one execution try for the Job, not a loop iteration.

The control plane owns durable execution coordination and history. The workload
handler owns the internal scenario and its workload-specific resources, consistent
with ADR-0004 and ADR-0005. This does not select a process-isolation model or make
browser contexts, credentials, or sessions core entities.

First-class durable workflow orchestration is deferred. A future orchestration
layer may compose Jobs; standalone Jobs must remain useful without it. No workflow
tables, graph engine, or speculative orchestration abstractions are required now.

### Execution is observable and produces results

The product must support the current stage, progress counters, partial results,
and a final workload-specific result. Large files are represented through artifact
references. Not every workload has a meaningful percentage or partial result.

Stage and progress describe execution; they are not a recovery checkpoint.
Partial results describe useful work already produced and may remain useful when
execution fails or is cancelled. The exact output contracts, persistence model,
artifact storage, limits, and technical versus business success rules require
follow-up decisions. This ADR does not add fields to the Slice 1 request contract.

### Running cancellation is cooperative

Cancellation requests stopping at a safe point and preserving available partial
results. Request acceptance is distinct from confirmation that execution stopped.
Cancellation does not undo external effects already performed.

Pending cancellation, completion/cancellation races, unresponsive handlers,
timeouts, and exact state transitions must be defined before implementing the
cancellation slice.

### Pause is deferred

Pause is outside the first execution slice. Pausing while the process and its
execution state remain alive is the current candidate for a later feature, not an
accepted protocol. Its resource, capacity, and lease behavior remain open.

Resuming after process loss, machine restart, or migration to another Worker is
a separate open recovery requirement. It may need workload-specific checkpoints
and resource reconstruction. Neither durable resume nor the relationship between
resume and JobAttempt identity is decided here.

### Automatic retries are introduced separately

The initial execution path does not automatically retry failed or lost execution.
Worker loss must eventually be recorded rather than silently rerunning the work.
Exact loss detection, Job/JobAttempt states, late-report handling, and transaction
semantics must be decided before implementing that path.

Keep the distinction between Job and JobAttempt and preserve attempt history.
The existing multi-attempt model remains valid; do not enforce one attempt per
Job as a permanent schema invariant. ADR-0007's maximum-attempt default of one
continues to apply. A future retry creates another execution attempt rather than
rewriting an earlier attempt's history.

Retry eligibility, limits, backoff, policy ownership, manual retry, and checkpoint
reuse are deferred. No generic retry engine or speculative policy schema is
required now. Transport retries and submission idempotency are separate from
re-executing workload effects; absence of automatic retries is not an exactly-once
guarantee.

## Consequences

- Simple and stateful scenarios share the same execution model.
- Progress, results, and cooperative cancellation are accepted requirements that
  can be delivered in separate slices.
- Worker registration alone does not validate the full scenario execution model.
- The first execution path can be bounded without preventing future retries.
- Recovery of in-memory browser/session state is not promised by a Lease or Job.
- Schema changes follow concrete protocol decisions and their implementation
  slices; this decision itself requires no migration.

## Follow-up Decisions

- Worker identity, supported workload routing, capacity, and resource isolation.
- Claiming, lease maintenance, loss detection, finalization, and duplicate reports.
- Input/output versioning, secrets, artifacts, progress ordering, and retention.
- Cancellation transitions and races; later pause and recovery semantics.
- Automatic retry policy and external-effect reconciliation.

ADR-0006 and ADR-0007 remain authoritative for existing submission behavior.
