# Domain Glossary

This glossary defines the shared language of Synestra. It intentionally does
not assign lifecycle ownership or aggregate boundaries that have not yet been
decided.

| Term | Meaning | Not | Confirmed semantics | Still undecided |
|---|---|---|---|---|
| `JobDefinition` | Registered type of executable work | A submitted execution instance | Has a stable UUID identity and a unique immutable machine-readable type; disabling blocks new submissions but does not affect existing jobs | Who manages definitions |
| `Job` | One logical unit of submitted work | One physical execution | References its `JobDefinition` by ID, snapshots its type, can have multiple attempts and preserves their history; submission uses server-owned UUID v7 identity and UTC creation time | Exact status derivation, cancellation and retry semantics beyond Slice 1 |
| `JobAttempt` | One execution try for a Job | The retry policy itself | Belongs to one Job and records one execution outcome | Exact creation point and state after lease expiration |
| `Lease` | Time-bounded exclusive right for a Worker to execute an attempt | A permanent lock or execution result | Has acquisition and expiration times and cannot permanently assign work | Renewal, expiration recovery and late-result behavior |
| `Worker` | Registered worker agent with identity, liveness and finite capacity | An OS thread, HTTP request, browser instance or individual CEF subprocess | May disappear and must periodically report liveness | Identity protocol, trust model, process lifetime and capacity accounting |

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
