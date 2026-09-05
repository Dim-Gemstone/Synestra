# Project Status

This document captures the current design state of Synestra.

It distinguishes between:

- **Accepted** — decisions or concepts that are already implemented or explicitly chosen as the current direction.
- **Proposed** — likely directions that have been discussed but are not yet binding.
- **Open** — unresolved questions that must not be silently decided during implementation.
- **Rejected** — approaches that were considered and are not part of the current direction.
- **Deferred** — valid ideas intentionally postponed until they become relevant.

This document is not a replacement for ADRs. Important accepted decisions should gradually receive dedicated records under `docs/decisions/`.

---

## Accepted

| Decision / Fact | Status | Notes |
|---|---|---|
| Synestra is a distributed work execution system | Accepted | It is not merely an HTTP API or CRUD layer over jobs. The core model is based on workers, attempts, leases, capacity, retries, and recovery. |
| The initial product form is an independent execution control-plane service | Accepted direction | Reusable libraries or a framework may emerge from concrete reuse needs later, but are not the current design target. |
| The control plane is independent of a specific workload | Accepted | Core scheduling and execution semantics must not depend on CEF or browser automation. |
| Browser / CEF automation is the first concrete workload | Accepted | It provides a real execution scenario for validating the general Synestra architecture. |
| `JobDefinition → Job → JobAttempt → Lease → Worker` are the core domain concepts | Accepted / Implemented | These concepts already form the current domain model. |
| A `Job` can have multiple `JobAttempt` instances | Accepted / Model implemented | Attempt history supports future retries; automatic retries are not implemented and are deferred by ADR-0008. Resume/attempt semantics remain open. |
| A Job can execute a complete scenario | Accepted / Not implemented | Worker-side handlers own internal stages, loops, and browser/API operations. See ADR-0008. |
| Execution exposes progress and results | Accepted / Not implemented | Current stage, counters, partial and final results are required; large files use artifact references. Detailed contracts remain open. |
| Running cancellation is cooperative | Accepted / Not implemented | Stop at a safe point and preserve available partial results; completed external effects are not undone. Exact transitions remain open. |
| Initial execution has no automatic retries | Accepted | Lost execution must be recorded without automatic rerun. Preserve multiple attempts and history for a separately defined retry policy. See ADR-0008. |
| A `Lease` represents temporary execution ownership | Accepted | A worker receives a time-bounded right to execute an attempt rather than permanent ownership of a job. |
| A Worker has finite capacity | Accepted concept | The exact meaning and accounting model of capacity are still open. |
| PostgreSQL is the primary durable store | Accepted / Implemented | PostgreSQL is also expected to participate in distributed coordination. |
| Entity Framework Core is used for persistence | Accepted / Implemented | EF Core mappings and migrations already exist. |
| Persistence is separated from the Domain project | Accepted | EF Core and PostgreSQL concerns must remain outside the Domain project. |
| Domain does not depend on Persistence, API, Aspire, or other infrastructure | Accepted | This is a core architectural boundary. |
| A dedicated Application layer exists | Accepted / Implemented | The SubmitJob use case provides the first application boundary between API and domain/persistence concerns. |
| Application depends on Domain | Accepted | Application use cases orchestrate domain behavior. |
| Persistence depends on Domain | Accepted | Persistence maps and stores domain entities. |
| API acts as the composition root | Accepted direction | API may compose Application and Persistence while keeping business workflow logic outside controllers/endpoints. |
| Workers communicate through the control-plane API | Accepted | Workers do not access the Synestra PostgreSQL database directly. Database coordination remains internal to the control plane. |
| Client API and Worker API are separate logical surfaces | Accepted direction | They may initially share one ASP.NET Core host but require distinct routes, contracts, and authorization boundaries. |
| .NET Aspire is used for local distributed application orchestration | Accepted / Implemented | Aspire currently starts and coordinates required development infrastructure. |
| PostgreSQL is started through Aspire | Accepted / Implemented | This is sufficient for the current development phase. |
| Database migrations are applied by a dedicated one-shot migration worker | Accepted / Implemented | Migration execution is separated from the API process. |
| Persisted timestamps use UTC | Accepted / Implemented | UTC is the standard time representation in the domain and persistence model. |
| Entity IDs use UUID v7 | Accepted / Implemented | UUID v7 is the current identifier strategy. |
| EF Core mappings live outside domain entities | Accepted / Implemented | Infrastructure-specific configuration must not shape the public domain model. |
| Domain entities expose private setters and enforce invariants | Accepted / Implemented | The model is intentionally not designed as a collection of mutable EF data records. |
| Jobs carry opaque JSON object payloads | Accepted / Implemented for Slice 1 | Slice 1 accepts objects up to 256 KiB and depth 32, rejects duplicate properties, applies an HTTP request-body bound, and performs no workload-specific schema validation. Persistence uses `jsonb`. See ADR-0007. |
| Jobs reference their creating definition by ID and snapshot its type | Accepted / Implemented | Clients use the unique immutable textual type; PostgreSQL enforces the internal relationship. See ADR-0006. |
| Disabled definitions reject new submissions only | Accepted | Existing jobs are unaffected by later definition disabling. See ADR-0007. |
| Slice 1 submission inputs and defaults are fixed | Accepted | Clients provide type, payload, and optional future `AvailableAtUtc`; the server owns ID and creation time and defaults priority to 0, maximum attempts to 1, and immediate availability to creation time. See ADR-0007. |
| Slice 1 submission has stable success, error, and transaction semantics | Accepted / Implemented | The thin Client API endpoint returns `201`, errors use RFC 9457 with stable codes, and definition validation plus Job insertion occur in one `READ COMMITTED` transaction with a definition-row `FOR SHARE` lock. See ADR-0007. |
| Slice 1A Job retrieval has a stable minimal contract | Accepted / Implemented | Clients can retrieve a persisted Job by ID. The response excludes payload and internal or future execution data; missing Jobs use `job_not_found`, and successful submission identifies the resource with `Location`. See ADR-0009. |
| Slice 1B supports opt-in idempotent submission | Accepted / Implemented | `Idempotency-Key` currently has global SubmitJob scope. PostgreSQL stores the exact request identity and original success snapshot with the Job; uniqueness and transaction advisory locks coordinate concurrent requests. Equivalent replay returns the same `201` and Location across API restarts; conflicting reuse returns `idempotency_key_conflict`. No-key submissions and GET retain their existing contracts. Keys remain at least as long as Jobs, without automatic cleanup. See ADR-0010. |
| Synestra follows a pragmatic domain-oriented architecture | Accepted direction | Domain modeling is used where useful without adopting full ceremonial DDD by default. |

---

## Proposed

The following ideas are considered plausible directions but are not yet binding architectural or domain decisions.

| Proposal | Why it remains Proposed |
|---|---|
| Domain methods such as `job.StartAttempt()`, `attempt.Succeed()`, `lease.Renew()`, and `worker.RecordHeartbeat()` | This matches the current domain model, but lifecycle ownership and aggregate boundaries are not yet fully defined. |
| Focused application services/use cases instead of full CQRS/Mediator infrastructure | Likely sufficient for the current system, but the exact Application architecture should emerge from real use cases. |
| PostgreSQL `FOR UPDATE SKIP LOCKED` for atomic job claiming | A strong candidate, but the final claim algorithm and transaction semantics have not been accepted yet. |
| Worker heartbeat with offline detection | The system requires liveness tracking, but heartbeat intervals, timeout thresholds, and recovery rules are not defined. |
| Lease renewal | The lease model strongly suggests renewal, but the protocol and failure semantics remain undefined. |
| Retry with backoff | Retries are expected, but retry policy, timing, classification, and ownership are not yet defined. |
| Live-process pause | Candidate for a later feature; resource, lease, and capacity semantics are undefined. Durable resume is a separate open question. |
| A worker agent supervises separate execution processes | This fits dynamic workloads and CEF process trees, but agent lifetime and process-isolation rules are not yet defined. |
| Worker-driven work acquisition through the Worker API | This is the simplest initial direction, but polling, long-polling, streaming, push, or broker-based delivery has not been selected. |
| Worker/browser pools and groups | These originate from the earlier browser-management concept and may be useful later, but they are not required by the current core. |
| Remote browser access | A potential future capability rather than a current core requirement. |
| Headless CEF workers | A plausible worker mode, but rendering and interactive access requirements remain unresolved. |

---

## Open Questions

These questions are intentionally unresolved.

Implementation must not silently choose semantics for them unless the relevant task explicitly includes making that decision.

| Area | Open Question |
|---|---|
| API consumers | Who are the intended consumers of the Synestra API? |
| Job definitions | Who creates and manages `JobDefinition` records? |
| Job submission | Who is allowed to submit jobs? |
| Worker trust | Are workers trusted internal processes, authenticated external clients, or both? |
| Worker identity | How does a worker obtain, persist, and prove its identity? |
| Worker process lifetime | Are worker agents long-lived supervisors, ephemeral single-job processes, or are both modes supported? |
| Execution isolation | Does each attempt receive a separate process, and who owns timeout, termination, cleanup, and result collection? |
| API deployment | Do Client API and Worker API remain one deployment or eventually become independently deployed services? |
| Work delivery | Does a worker poll, long-poll, stream, receive push notifications, or consume broker notifications before claiming work? |
| Job claiming | What is the exact atomic job-claiming algorithm? |
| Delivery guarantee | Is execution explicitly at-least-once, or does Synestra provide another guarantee? |
| Duplicate execution | Under which failure scenarios can the same logical job execute more than once? |
| Lease expiration | What state does an attempt enter when its lease expires? |
| Late completion | What happens when a worker reports success or failure after losing its lease? |
| Retry creation | When exactly is a new `JobAttempt` created? |
| Retry policy | How are maximum attempts, backoff, and recoverable/non-recoverable failures defined? |
| Cancellation | Given cooperative running cancellation, what are the exact transitions, Pending behavior, completion races, and unresponsive-handler rules? |
| Pause and resume | How would live-process pause work, and is durable continuation after restart or relocation required? How would resume relate to attempts? |
| Execution data | What are the result/artifact contracts, progress ordering, checkpoint compatibility, and technical versus business success rules? |
| Workload inputs | How are secrets delivered, input snapshots/versioning handled, and concurrent account access constrained? |
| Worker capacity | What does a capacity value represent: generic execution slots, browser instances, resource units, or something else? |
| Capacity accounting | When is capacity reserved and released, and which component owns that accounting? |
| Job status | How is `Job.Status` derived from or coordinated with `JobAttempt.Status`? |
| Scheduling beyond Slice 1 | Slice 1 accepts only optional delayed availability. Will recurring/cron jobs, deadlines, timeouts, or other scheduling inputs ever belong to the core? |
| Payload evolution | How are workload-specific schemas, contract versioning, and deeper security validation represented? |
| Transaction boundaries beyond submission | Which Application operations other than SubmitJob define database transaction boundaries? |
| Concurrency control | Which operations require pessimistic locking, optimistic concurrency, or both? |
| Application persistence boundary | Should Application depend on custom persistence abstractions, or can some use cases work with more direct infrastructure-specific interfaces? |
| Retention | How long are jobs, attempts, leases, and worker records retained? |
| Observability | Which correlation identifiers, traces, metrics, and operational events are required? |
| Authentication / authorization | What authentication and authorization model will protect control-plane operations? |
| Scale | What job throughput, worker count, latency, and history volume should the design target? |
| Browser execution | Should browser workers be headless-only, support native rendering, remote rendering, or multiple modes? |

---

## Rejected

These approaches are not part of the current direction.

| Approach | Status / Reason |
|---|---|
| Rebuilding the previous CEF desktop application one-to-one | Rejected. Synestra may reuse the execution scenario, but not the old application architecture or UI model. |
| Making Synestra Core intrinsically dependent on CefSharp or WinForms | Rejected. Browser execution must remain a workload-specific implementation. |
| Domain depending on Persistence or EF Core | Rejected. Infrastructure dependencies must point toward Domain, not the opposite direction. |
| Keeping orchestration and business workflow logic inside API controllers/endpoints | Rejected direction. API should remain a transport/composition boundary. |
| Introducing repositories, Mediator, full CQRS, domain events, or other DDD infrastructure by default | Rejected as a default approach. Such patterns should only be introduced when they solve a concrete problem. |
| Using in-memory locking as distributed worker coordination | Rejected. Coordination must remain correct across independent processes and machines. |
| Keeping EF migration orchestration directly inside AppHost | Superseded by the dedicated migration worker approach. |

---

## Deferred

Deferred items are not rejected. They are intentionally postponed until the current execution model is sufficiently mature.

| Item | Reason for Deferral |
|---|---|
| Production-grade container deployment strategy | Current development only requires Aspire to reliably start the local distributed system. |
| Development/production environment parity | Important long term, but not required before the core execution semantics exist. |
| Docker Compose or alternative deployment generation | Useful for future deployment, but not necessary for the current development phase. |
| Recurring / cron jobs | Delayed execution already exists conceptually through availability time; recurring scheduling can be introduced later if needed. |
| Automatic retries | A separate policy decision and slice will define eligibility, timing, limits, and external-effect safety. Initial execution records loss without rerunning work. |
| Pause | Outside the first execution slice; live-process pause is only a candidate. Durable resume is not promised. |
| First-class Workflow orchestration | May later compose standalone Jobs. Internal scenario stages and loops do not require an orchestration engine. |
| Worker groups / browser pools | Useful only after basic worker registration, capacity, claiming, and lease semantics work. |
| Remote interactive browser rendering | A future browser-worker feature rather than a control-plane prerequisite. |
| Advanced worker resource models | Generic capacity should be understood first before introducing CPU/memory/browser-specific resource scheduling. |

---

## Current Architectural Interpretation

The intended logical dependency direction is:

```text
API
  ↓
Application
  ↓
Domain
```

Persistence also depends on Domain:

```text
Persistence
    ↓
  Domain
```

The API project may act as the composition root and therefore reference both Application and Persistence:

```text
             ┌─────────────┐
             │     API     │
             └──────┬──────┘
                    │
             ┌──────┴──────┐
             ▼             ▼
       Application     Persistence
             │             │
             └──────┬──────┘
                    ▼
                  Domain
```

This does not mean that Application business logic should depend directly on the concrete Persistence implementation.

The exact Application-to-Persistence boundary remains subject to implementation experience.

---

## Current Product Interpretation

The current working interpretation is:

> Synestra is a workload-agnostic distributed execution control plane for coordinating asynchronous work across independently running workers.

Jobs represent logical units of work.

Each physical execution is represented by a separate attempt.

Workers receive temporary leases rather than permanent ownership of jobs.

Workers have finite execution capacity and may disappear unexpectedly.

The system must therefore support durable execution state and execution history.
The initial execution path records lost execution without automatic retries;
retry policy and durable continuation are subsequent decisions. ADR-0008 accepts
scenario execution, progress/results, and cooperative cancellation as product
requirements. Pause and first-class Workflow orchestration are deferred.

Browser / CEF automation is the first concrete worker workload used to validate this execution model, but browser-specific concerns must not define the Synestra core architecture.
