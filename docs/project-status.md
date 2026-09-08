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
| Execution exposes progress and results | Accepted / Terminal reporting and Client read implemented | ADR-0013 persists a bounded result/error; ADR-0016 exposes terminal completion through GET. Full Client execution qualification remains 2F.2; progress, partial results and artifacts remain unimplemented. |
| Running cancellation is cooperative | Accepted / Not implemented | Stop at a safe point and preserve available partial results; completed external effects are not undone. Exact transitions remain open. |
| Initial execution has no automatic retries | Accepted | Lost execution must be recorded without automatic rerun. Preserve multiple attempts and history for a separately defined retry policy. See ADR-0008. |
| A `Lease` represents temporary execution ownership | Accepted | A worker receives a time-bounded right to execute an attempt rather than permanent ownership of a job. |
| A Worker has finite capacity | Accepted / Reservation and completion release implemented | Each unreleased, unexpired Lease reserves one slot across all sessions under ADR-0012; ADR-0013 completion releases it atomically. No standalone release endpoint exists. |
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
| Slice 2A supports Worker registration and liveness | Accepted / Implemented | Worker API PUT atomically registers or replaces session, configuration and capability set. Durable accepted-session history fences replaced sessions for both delayed PUT and heartbeat; liveness persists monotonic LastSeenAtUtc. UUID v7 WorkerId is stable and SessionId changes per launch; neither authenticates the caller. Trusted/private deployment only, without authentication. Application owns READ COMMITTED transactions; PostgreSQL advisory/row locks serialize operations. A 10-second heartbeat and derived 30-second offline threshold come from focused Application options. Legacy rows can be adopted without backfill. See ADR-0011. |
| Synestra follows a pragmatic domain-oriented architecture | Accepted direction | Domain modeling is used where useful without adopting full ceremonial DDD by default. |
| Slice 2B supports atomic claim and durable ownership | Accepted / Implemented | Current online Worker sessions claim one available, exactly supported Pending Job through the Worker API. Application owns READ COMMITTED; Worker FOR UPDATE protects capacity and session state, then Job FOR UPDATE SKIP LOCKED applies priority/availability/creation/UUID ordering. Job.StartAttempt creates a Running attempt and a session-bound Lease atomically with Pending -> Running. Default duration is 30 seconds; claim never changes liveness. Expired leases stop consuming slots without finalization or retry. API returns the original eight execution fields plus ADR-0013 leaseToken after commit, identical 204 for no work/capacity and stable Problem Details. PostgreSQL and API concurrency/restart tests cover the path. Legacy null-session leases are preserved, with checks/FKs for non-null bindings. See ADR-0012. |
| Slice 2C supports Lease renewal and execution reporting | Accepted / Implemented | ADR-0013 adds per-Lease random token/hash fencing, monotonic renewal independent of heartbeat, strict success/failure reports and durable ReportId replay. Job coordinates terminal Job/attempt state and matching finish/release times; PostgreSQL transactions commit snapshot and capacity release together. Ordered row locks and rollback cleanup protect races and scope reuse. Legacy tokenless leases remain fenced; Client GetJob is unchanged. |
| Slice 2D records lost execution without retry | Accepted / Implemented | ADR-0014 defines atomic loss finalization, ordered nonblocking locks, bounded keyset discovery and independent candidate transactions. The API hosted service is enabled by default: immediate first pass, 5-second delay after each pass, at most 100 candidates. Validated configuration allows disablement; scopes, cancellation and failure delays protect host operation. Restart and simultaneous-host tests verify persisted work is rediscovered and terminal rows are not rewritten. Legacy compatibility, Worker replay/error precedence and Client response shape are preserved. Eventual recording requires an enabled host, database availability and locks that eventually release. |

---

ADR-0015 accepts the minimal Worker runtime and bounded test workload. Units 2E.1
through 2E.3 implement identity/liveness and capacity-one execution of
`test.bounded-sum.v1`, including claim, renewal, frozen completion and bounded
shutdown. The Worker uses HTTP only and has no control-plane project/database
dependency. Tests verify result/error persistence, lease release, API restart and
finalizer rejection without outcome overwrite. Bounded transport recovery preserves
registration identity, confirmed lease budgets and frozen reports, including replay
after committed response loss and API restart. Unit 2E.4 adds local Aspire startup
ordering, explicit development definition preparation and real process tests for
execution, renewal, completion, graceful stop, crash/restart, identity locking and
loss finalization. Slice 2E is implemented; Slice 2 remains incomplete.

## Proposed

The following ideas are considered plausible directions but are not yet binding architectural or domain decisions.

| Proposal | Why it remains Proposed |
|---|---|
| Focused application services/use cases instead of full CQRS/Mediator infrastructure | Likely sufficient for the current system, but the exact Application architecture should emerge from real use cases. |
| Retry with backoff | Retries are expected, but retry policy, timing, classification, and ownership are not yet defined. |
| Live-process pause | Candidate for a later feature; resource, lease, and capacity semantics are undefined. Durable resume is a separate open question. |
| A worker agent supervises separate execution processes | This fits dynamic workloads and CEF process trees. ADR-0015 selects in-process execution only for its bounded built-in test handler; general isolation is still open. |
| Worker wakeup/delivery beyond 2E | ADR-0015 implements simple polling for the minimal agent. Long-polling, streaming, push and broker notifications remain undecided. |
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
| Worker trust | ADR-0011 temporarily requires a trusted/private boundary without authentication. Which future authentication and authorization mechanism will prove identity and govern replacement? |
| Worker process lifetime | ADR-0015 chooses a long-lived capacity-one agent for 2E. Are other lifetime modes needed for later workloads? |
| Execution isolation | ADR-0015 bounds one trusted in-process test handler. Which later workloads require separate processes, and who owns their termination and resource lifecycle? |
| API deployment | Do Client API and Worker API remain one deployment or eventually become independently deployed services? |
| Work delivery | ADR-0015 selects polling for 2E. Do later workloads require long-polling, streaming, push or broker notifications before claiming work? |
| Delivery guarantee | Is execution explicitly at-least-once, or does Synestra provide another guarantee? |
| Duplicate execution | Under which failure scenarios can the same logical job execute more than once? |
| Retry creation | Claim creates an attempt for a Pending Job under ADR-0012. How will a future retry policy make a failed/lost Job eligible again? |
| Retry policy | How are maximum attempts, backoff, and recoverable/non-recoverable failures defined? |
| Cancellation | Given cooperative running cancellation, what are the exact transitions, Pending behavior, completion races, and unresponsive-handler rules? |
| Pause and resume | How would live-process pause work, and is durable continuation after restart or relocation required? How would resume relate to attempts? |
| Execution data | ADR-0013 defines Worker completion data and ADR-0016 defines Client terminal reads. What are the artifacts, progress, checkpoint and workload-specific business success contracts? |
| Workload inputs | How are secrets delivered, input snapshots/versioning handled, and concurrent account access constrained? |
| Capacity accounting | Claim reserves and completion releases slots under ADR-0012/0013. What execution resource and future control rules will apply? |
| Scheduling beyond Slice 1 | Slice 1 accepts only optional delayed availability. Will recurring/cron jobs, deadlines, timeouts, or other scheduling inputs ever belong to the core? |
| Payload evolution | How are workload-specific schemas, contract versioning, and deeper security validation represented? |
| Execution transaction boundaries | Existing use cases own focused transactions; ADR-0014 adds nonblocking Worker -> Job -> attempt -> Lease finalization. Which future use cases will need other transaction contracts? |
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

Slices 1, 1A, 1B, 2A, 2B, 2C and 2D are implemented. Worker registration, liveness,
claim, token-fenced renewal and idempotent execution reporting survive API restarts.
Reported success/failure atomically finalizes Job/attempt, persists a small result
or error, and releases Lease capacity. Late completion before finalization is
accepted. Unit 2D.1 adds internal, atomic loss finalization for one expired Lease,
including legacy ownership, concurrency and rollback behavior. Unit 2D.2 adds a
bounded internal sweep with read-only keyset discovery, independent transactions,
failure isolation and safe restart/reset. Unit 2D.3 runs that sweep automatically
in enabled API hosts, including after restart and across concurrent instances.
Slice 2 remains incomplete.

Slice 2E is implemented: ADR-0015 units 2E.1 through 2E.4 provide the minimal Worker,
bounded workload execution, lease maintenance, persisted completion, bounded
transport recovery/replay, local Aspire orchestration and real process qualification.
Unit 2F.1 implements ADR-0016 Client terminal reads with a separate Application
model, coherent PostgreSQL projection, bounded output and explicit legacy
fallbacks. POST and durable submission replay remain unchanged. Slice 2F and
Slice 2 remain incomplete until unit 2F.2 qualifies the Client submit-to-result
path with real Worker/process execution, replay and loss behavior. A minimally
useful product also needs a concrete workload beyond the bounded synthetic handler.

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
