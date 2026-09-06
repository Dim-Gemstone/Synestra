# Domain Glossary

This glossary defines the shared language of Synestra. It intentionally does
not assign lifecycle ownership or aggregate boundaries that have not yet been
decided.

| Term | Meaning | Not | Confirmed semantics | Still undecided |
|---|---|---|---|---|
| `JobDefinition` | Registered type of executable work | A submitted execution instance | Has a stable UUID identity and a unique immutable machine-readable type; disabling blocks new submissions but does not affect existing jobs | Who manages definitions |
| `Job` | One logical unit of submitted work | One physical execution | References its definition by ID, snapshots its type, preserves attempt history and uses server-owned UUID v7 identity and UTC creation time; atomic claim transitions Pending -> Running through StartAttempt under ADR-0012 | Completion/status derivation, cancellation and retry semantics |
| `JobAttempt` | One execution try for a Job | The retry policy itself | Created Running by Job during atomic claim, with number max(history) + 1 and start time equal to Lease acquisition | Completion outcome and state after lease expiration |
| `Lease` | Time-bounded exclusive right for a Worker session to execute an attempt | A permanent lock, credential or execution result | Claim persists WorkerId and current SessionId, acquisition and expiration; one unreleased, unexpired Lease reserves one slot | Renewal, explicit release, expiration recovery and late-result behavior |
| `Worker` | Registered worker agent with identity, liveness and finite capacity | An OS thread, HTTP request, browser instance or individual CEF subprocess | Stable UUID v7 identity, replaceable UUID v7 process session, exact supported types and positive execution-slot limit; ADR-0011 liveness and ADR-0012 atomic claim | Future authentication, process lifetime and release/reporting protocol |

## Working execution terminology

The following terms describe the current topology without fixing process
lifetime or deployment details.

| Term | Meaning | Boundary |
|---|---|---|
| Worker agent | Process that communicates with the Worker API, reports liveness and capacity, and supervises execution | Does not access the control-plane database directly |
| Execution process | Process or process tree that performs a particular workload under worker supervision | Does not own queue coordination semantics |
| Client API | Product-facing API used by the frontend and other clients | Expresses user and product use cases |
| Worker API | Machine-to-machine API used for worker coordination | Expresses registration, work acquisition, lease, and result protocol |

For a browser workload, one execution process may supervise or contain a tree
of CEF subprocesses. Those subprocesses are workload implementation details,
not individual Synestra Workers.

See `job-lifecycle.md` for confirmed and undecided lifecycle behavior.

## Worker registration terminology

ADR-0011 implements Slice 2A through the Worker API in a trusted/private boundary.
WorkerId is stable across launches; SessionId identifies one process launch.
Neither is an authentication credential. Registration with a different SessionId
replaces the current session; subsequent requests from the old session conflict,
including delayed registration PUTs. Durable accepted-session history distinguishes
an unseen process session from a previously replaced one. Same-session registration
continues to update desired state.

Supported types are a full, ordinal case-sensitive capability set independent of
JobDefinition existence or enabled state. Capacity is the maximum concurrent
execution-slot count; active Leases reserve slots under ADR-0012. Registration is also
liveness confirmation. LastSeenAtUtc never moves backwards; offline is derived at
30 seconds since last seen, with a recommended heartbeat every 10 seconds. There
is no persisted online flag, monitor, Worker read endpoint or loss recovery yet.

## Atomic claim terminology

ADR-0012 implements Slice 2B: a current, online Worker session claims one Pending
Job available at server UTC with an exactly supported type. Priority descends;
availability, creation and PostgreSQL UUID order ascend. Locked candidates are
skipped. Job transition, Running attempt creation and session-bound Lease insertion
commit together before returning execution input. Claim does not confirm liveness.

An active Lease has ReleasedAtUtc null and ExpiresAtUtc strictly later than server
UTC. Count all sessions of a Worker, including legacy null-session leases. Exact
expiration frees the slot without finalizing the Running attempt or creating a
retry. Reducing capacity or replacing the session never deletes existing leases.
New leases require UUID v7 session binding; nullable schema preserves legacy rows.
Ownership is durable, but workload execution and reporting are still unimplemented.

## Scenario execution terminology

ADR-0008 accepts a Job as a complete scenario, including internal stages and
loops. These terms describe requirements, not additional implemented entities.

| Term | Meaning | Boundary |
|---|---|---|
| Workload handler | Worker-side implementation of a requested scenario | Owns internal steps and workload-specific resources |
| Stage | Description of the current part of execution | Does not automatically create a Job or workflow node |
| Progress | Counters or other indication of advancement | Does not guarantee enough state for recovery |
| Partial result | Useful work already produced | May exist even when execution is cancelled or fails |
| Final result | Workload-specific output associated with completed execution | Exact contract and business success rules remain open |
| Artifact reference | Reference to a large input or output file | Storage, access, and retention contracts remain open |
| Checkpoint | State sufficient for workload-specific continuation | Deferred; distinct from progress and partial results |
| Live-process pause | Candidate suspension while execution state remains alive | Deferred; does not promise restart recovery |
| Durable resume | Continuation after execution state is lost or relocated | Open; attempt identity and resource reconstruction are undecided |
| Workflow orchestration | Durable coordination of work and its dependencies | Deferred; may later compose standalone Jobs |

The initial execution path performs no automatic retries. Multiple attempts per
Job remain part of the model for future retry policy; their history is preserved.

## Submit job terminology

For Slice 1, an immediate Job has `AvailableAtUtc` equal to its server-generated
`CreatedAtUtc`. A client may instead supply a later UTC availability time.
Availability is not a recurring schedule, deadline, or execution timeout.

The Job payload is an opaque JSON object from the control plane's perspective.
Acceptance validates its JSON shape and resource limits but does not validate a
workload-specific schema. See ADR-0007 for the authoritative submission
contract, defaults, errors, and transaction semantics.
