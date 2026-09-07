# ADR-0015: Minimal Worker Runtime and Bounded Test Workload

**Status:** Accepted

## Context and implementation boundary

ADR-0011 through ADR-0014 provide durable registration, ownership, reporting and
automatic loss finalization. Slice 2E adds a real agent and one synthetic workload
without expanding ADR-0009's Client read contract. ADR-0005 and ADR-0008 retain
the API boundary and workload-agnostic control plane.

This ADR accepts the contract for all of Slice 2E. Only unit 2E.1 is implemented:
the executable, identity, registration, heartbeat and idle shutdown. Claim,
handler execution, renewal, completion, transport retries and Aspire wiring are
not implemented by this unit. Slices 2E and 2 remain incomplete.

## Decision

### Process, identity and dependencies

Use one long-lived .NET 10 Generic Host executable, Synestra.Worker, with capacity
1 and the exact supported type `test.bounded-sum.v1`. There is no ephemeral mode,
prefetch, execution queue or concurrent handler. The agent uses HTTP Worker API
contracts only. It references no Domain, Application, Persistence or API project,
and receives no PostgreSQL connection string. Keep small transport DTOs local;
contract tests protect compatibility without introducing a reusable SDK.

Configuration uses `Worker:ApiBaseAddress`, `Worker:StateDirectory`, and
`Worker:Name` (default `synestra-worker`). API address is an absolute HTTP(S)
origin, without credentials, path prefix, query or fragment. State directory is
explicit and absolute. Name follows ADR-0011's text limits and valid Unicode.
Validate before sending any registration. Capacity/type are fixed, not settings.
The trusted/private deployment restriction remains; no authentication is added.

Generate an RFC UUID v7 WorkerId once and persist it before registration. Store
its 36-byte D representation in `worker-id` in the state directory. Existing
invalid, empty or partially written identity causes startup failure without
replacement. Hold `worker.lock` open exclusively for the agent lifetime; retain
the file after closing it. Two local processes cannot share that directory.
Use a persistent local filesystem supporting exclusive file sharing; shared
network storage is not the coordination mechanism. Distinct state directories
represent distinct Workers. Do not copy a live Worker's identity to another agent.

Generate a new UUID v7 SessionId for each launch. Persist neither session nor
lease tokens nor pending reports. API restart does not change the live session;
agent restart does. A new session never adopts, resumes or reports an old attempt.
Old-session active leases still reserve capacity under ADR-0012. A replaced
session stops rather than generating another session to displace its replacement.

### Liveness and work acquisition

Register first, send an initial heartbeat, then acquire work when execution is
implemented. Heartbeats continue independently of execution and renewal. Use the
interval returned by registration. Wait that interval after each completed
heartbeat; no overlap, catch-up ticks or liveness from claim/renewal is assumed.
Validate the registration's identity, desired state, UTC timestamps and positive
timer-compatible heartbeat interval smaller than the offline threshold.

Claim only with the current session and an empty local slot. Empty 204 means wait
one second before the next poll. `worker_offline` requires a successful heartbeat
before another claim. An acknowledged completion and local handler termination
are required before the next normal claim; a lost execution must first stop
locally. Do not reclaim or re-execute the old Job.

### Bounded deterministic workload (2E.2, not implemented in 2E.1)

The in-process handler accepts exactly `values` and `durationMs`. Values is an
array of 1 through 1024 integers in [-1000000, 1000000]; durationMs is an integer
in [0, 60000]. Compute the sum in Int64 and await the requested duration through
TimeProvider. Return exactly an object with `count` and `sum`. Output is independent
of clocks, machine identity and randomness. Validate input before execution;
`invalid_workload_input` is a failed completion, not a submission contract change.
Ordinary handler failure uses a bounded fixed `workload_execution_failed` error,
without exception contents. Failure report messages must be fixed safe text.

One execution has a 65-second local watchdog, bounded operations/memory/result,
and cooperative local stop points. It performs no external network effects,
file operations, subprocess execution or arbitrary user code. A watchdog expiry
stops local work without inventing a control-plane timeout/cancellation outcome;
unreported execution remains subject to ADR-0014. Normal resource disposal is
part of process ownership, not a cleanup/retention feature.

In-process execution is accepted only for this trusted built-in bounded handler.
It is not hard OS isolation or a real-time termination guarantee for arbitrary
code. Browser execution and separate execution-process supervision require later
decisions. No workload-specific concepts enter Domain or persistence.

### Lease maintenance (2E.2, not implemented in 2E.1)

Use injectable TimeProvider for timers and monotonic elapsed time. Initial lease
duration comes from expiresAtUtc minus acquiredAtUtc, not a hardcoded 30 seconds.
Anchor the conservative local deadline to the monotonic timestamp before sending
claim, adding that duration minus a five-second safety margin. A response received
after the deadline must not start execution. Too short a lease cannot authorize
local work under this margin.

Only acknowledged renewal adds the received increase in expiresAtUtc to the
deadline. Unknown renewal commits provide no local budget. Initial renewal cadence
is one third of the lease duration, further bounded by remaining time; a pending
request cannot outlive the local deadline. Client wall-clock regression must not
extend ownership. This estimate assumes a progressing server clock: abrupt server
UTC changes can invalidate local timing. Authoritative server ownership checks
and finalization remain necessary; no instantaneous physical fencing is promised.

Heartbeat and renewal are independent. An offline agent can renew valid ownership
under ADR-0013. Lease conflicts stop local execution, never resume it. Serialize
renewal and completion for the lease; once reporting begins, issue no new renewals.

### Transport failures and completion replay

Each HTTP operation, including reading its body, has a five-second timeout through
TimeProvider. Bound response buffering to 1 MiB. Do not follow redirects or enable
cookies, generic retries or the shared ServiceDefaults resilience handler. Log
identifiers, known protocol codes and exception types only; never response contents,
payload, tokens, reports or exception messages/objects.

In 2E.1 and 2E.2, any transport/protocol failure stops the process with exit 1.
Unit 2E.3 introduces only these operation-specific recovery rules:

- Repeat registration with the same WorkerId, SessionId and desired state.
- Repeat renewal only within the last confirmed ownership budget.
- Freeze one UUID v7 ReportId and its completion data; repeat that exact report
  after ambiguous completion, including API restart, without running the handler.
- Allowed retries have at most three attempts, one-second waits, and a total
  fifteen-second budget, additionally constrained by lease/shutdown deadlines.
- An ambiguous claim (timeout, disconnect, 5xx or unreadable success response)
  stops the process without repeating claim. Its possibly committed execution
  is left to ADR-0014; there is no API for recovering the once-returned token.

Session replacement stops the whole agent. While executing, lease_expired,
lease_ownership_lost and lease_not_active stop the local handler and that lease's
maintenance. A handler stopped for loss does not invent a completion outcome.
An already frozen report can still be delivered after expiry under ADR-0013;
late completion is not renewed execution. A lease_not_active response alone does
not disprove a previous completion commit. Replay resolves a pending report.

attempt_already_finalized ends reporting without changing the terminal outcome.
completion_report_conflict is a fatal protocol error. Invalid requests, unexpected
404s and unknown protocol responses fail closed rather than creating identity or
adopting execution. Report exhaustion is fatal with the completion still uncertain.
Workload failures that were successfully reported do not stop the long-lived agent.
These transport repeats are distinct from automatic workload retries.

### Shutdown and restarts

Stop new claims immediately. Cancel an unfinished handler locally without sending
a synthetic failed/cancelled outcome. If a report is already frozen, allow bounded
delivery within the total ten-second shutdown budget. Await owned tasks and dispose
timers, HTTP operations and identity ownership; no fire-and-forget execution remains.
In 2E.1 shutdown cancels heartbeat requests and idle waits without a drain phase.
Normal requested shutdown returns exit 0; fatal startup/protocol/transport failures
return exit 1. Unacknowledged work follows existing server finalization semantics.

A short API outage can be tolerated only under the later bounded recovery rules
and confirmed lease budget. A longer outage stops local execution; the agent never
assumes an extension. API restart preserves persisted ownership and report replay.
Agent restart replaces session and cannot recover its previous in-memory execution.
Finalization records loss but does not itself terminate the agent or prove absence
of external effects. First committed terminal transition wins under ADR-0014.

### Verification, delivery and exclusions

Use Worker unit/host tests with controlled time and HTTP gates; real API/PostgreSQL
tests verify liveness, fencing and later execution/reporting. Ordinary protocol
factories explicitly disable the finalizer. Separate enabled-host tests verify loss
and races. Preserve Testcontainers database-per-test isolation, the pinned image,
MTP/xUnit conventions and existing PostgreSQL concurrency coverage. Time tests
observe timer registration/operation completion before advancing clocks, without
long real sleeps. Separate-process qualification and Aspire integration are 2E.4.

Definition bootstrap belongs to explicit dev/test preparation outside Worker. No
definition-management API, automatic startup seed or general ownership policy is
introduced. Worker does not migrate or connect to the database.

Units: 2E.1 identity/liveness; 2E.2 bounded execution/lease/completion; 2E.3 bounded
transport recovery/replay; 2E.4 local orchestration and process qualification.
Only selected units may be implemented; acceptance of this ADR does not mark
later units complete. Slice 2F remains a separate increment.

No schema change, Client outcome/result expansion, automatic workload retry,
cancellation/pause protocol, artifacts/progress, authentication, broker, scheduler
framework, cleanup/retention, durable spool or browser runtime is included.
