# Domain Glossary

This glossary defines the shared language of Synestra. It intentionally does
not assign lifecycle ownership or aggregate boundaries that have not yet been
decided.

| Term | Meaning | Not | Confirmed semantics | Still undecided |
|---|---|---|---|---|
| `JobDefinition` | Registered type of executable work | A submitted execution instance | Has a stable UUID identity and a unique immutable machine-readable type; disabling blocks new submissions but does not affect existing jobs | Who manages definitions |
| `Job` | One logical unit of submitted work | One physical execution | References its definition by ID, snapshots its type and preserves attempt history; StartAttempt owns Pending -> Running, SucceedAttempt/FailAttempt coordinate Worker completion, and AbandonAttempt records loss as Failed under ADR-0014 | Cancellation and retry semantics |
| `JobAttempt` | One execution try for a Job | The retry policy itself | Created Running during claim with max(history) + 1; completion stores success result or failure error; loss records Abandoned with an expiry error and a finish time matching Job completion | Future retry and cancellation transitions |
| `Lease` | Time-bounded exclusive right for a Worker session to execute an attempt | A permanent lock or execution result; LeaseId is not a credential | Worker/session-bound, renewable before expiration, released atomically on completion or loss finalization; one unreleased/unexpired Lease consumes a slot | Any future standalone release protocol |
| `Worker` | Registered worker agent with identity, liveness and finite capacity | An OS thread, HTTP request, browser instance or individual CEF subprocess | Stable UUID v7 identity and replaceable process session; exact supported types; positive capacity; only registration/heartbeat confirm liveness; ADR-0015 adds a long-lived capacity-one agent with implemented identity/liveness | Future authentication and general execution process lifetime/isolation |

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

ADR-0015's minimal executable persists WorkerId in an explicit local state
directory and holds exclusive ownership of that directory until shutdown. Every
launch creates a fresh SessionId. Unit 2E.1 only registers and heartbeats; the
accepted bounded in-process handler and claim/renewal/reporting loops are not yet
implemented. This limited isolation decision does not select a browser process model.

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
is no persisted online flag, monitor or Worker read endpoint. Internal loss
finalization below does not use offline status as eligibility.

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
Ownership and reporting are durable, but actual workload execution is unimplemented.

## Renewal and completion terminology

ADR-0013 implements Slice 2C. A Lease token is a random 256-bit per-Lease fencing
secret, returned as canonical unpadded base64url once at claim. PostgreSQL stores
only SHA-256 hash; tokenless legacy leases cannot renew/report. This does not add
Worker authentication or change the trusted/private deployment limitation.

Renewal maintains execution ownership, independently of Worker availability for
new claims. It extends expiration monotonically without updating LastSeenAtUtc.
ReportId is a client-generated RFC UUID v7 completion identity scoped to an attempt.
The persisted completion snapshot holds the original success response; structural
JSON equivalence permits replay despite object order/formatting differences.

Completion atomically finalizes Job/attempt, persists a small success result or
failure error, and releases Lease capacity. Result is an object up to 64 KiB in
received UTF-8, depth 32, without duplicate names; workload-specific validation is
absent. Late completion while Running/unreleased may be accepted after expiration,
without renewing ownership. It is not lost-execution recovery. Client-visible
outcome/result remains unimplemented.

## Lost-execution terminology

ADR-0014 defines Slice 2D. Unit 2D.1 implements internal finalization of one
expired Lease. The Job becomes Failed, its Running attempt becomes Abandoned,
and the Lease is released with matching decision timestamps and the fixed
`execution_lease_expired` error. There is no synthetic Worker report or result.
Exact expiration is eligible; no grace period, offline requirement or current
session/token check applies. Old-session and legacy tokenless Leases can qualify.
Inconsistent relationships are skipped without repair. Completion and loss share
ordered locks; the first committed terminal transition wins without overwriting
Worker completion replay. No attempt is retried, and external effects remain uncertain.

Unit 2D.2 adds an internal bounded sweep of expired candidates. Its cursor is a
last expiration/LeaseId pair plus a fixed traversal cutoff, used only to advance
discovery and revisit skipped/failed work after each cycle. Every candidate still
needs its own transaction and locked eligibility check.

Unit 2D.3 runs this sweep automatically in the API host, enabled by default, with
an immediate first pass, up to 100 candidates and a 5-second delay after each pass.
This is a server background process, not a Worker agent. Restart discards the
traversal cursor and rediscovers persisted work; multiple hosts coordinate through
the same row locks. Eventual recording requires an enabled host, available database
and locks that eventually release. Disabled deployments provide no such promise.
Slice 2D is implemented; Slice 2 still needs Worker execution and Client outcome/result.

## Scenario execution terminology

ADR-0008 accepts a Job as a complete scenario, including internal stages and
loops. These terms describe requirements, not additional implemented entities.

| Term | Meaning | Boundary |
|---|---|---|
| Workload handler | Worker-side implementation of a requested scenario | Owns internal steps and workload-specific resources |
| Stage | Description of the current part of execution | Does not automatically create a Job or workflow node |
| Progress | Counters or other indication of advancement | Does not guarantee enough state for recovery |
| Partial result | Useful work already produced | May exist even when execution is cancelled or fails |
| Final result | Workload-specific output associated with successful execution | ADR-0013 persists a bounded object result through Worker API; business rules and client-visible result remain open |
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
