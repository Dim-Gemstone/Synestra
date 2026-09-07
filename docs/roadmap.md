# Roadmap

Slices 1, 1A, 1B, 2A, 2B, 2C, and 2D are implemented. Slice 2 remains incomplete.
ADR-0011 defines registration/liveness and ADR-0012 defines atomic claim and
execution ownership; ADR-0013 defines token fencing, renewal and completion reports.
ADR-0014 defines implemented automatic loss finalization through an atomic
single-execution path, bounded discovery and an enabled-by-default hosted service.
ADR-0008 accepts the execution direction and
the requirements illustrated in `execution-scenarios.md`. The later slices below are
a proposed delivery sequence, not implemented features or approval of their open
protocol decisions. Resolve the listed decisions before implementing each slice.

## Slice 1 — Submit a job

A client can submit a Job for an enabled JobDefinition.

Done when:

- application use case exists;
- invalid definition is rejected;
- disabled definition behavior is explicitly defined;
- Job is persisted;
- API endpoint exists;
- domain tests exist;
- PostgreSQL integration test exists.

Not included:

- worker dispatch;
- retries;
- cancellation;
- submission idempotency.

Decisions resolved before implementation:

- behavior for submitting work against a disabled definition — resolved by ADR-0007;
- which scheduling and retry inputs the client may provide — resolved by ADR-0007;
- ownership of job ID and creation timestamp generation — resolved by ADR-0007;
- minimum JSON payload validation and size rules — resolved by ADR-0007;
- submission response, error, and transaction contracts — resolved by ADR-0007.

Implementation requirements:

- add a focused `SubmitJob` Application use case and make it the transaction
  boundary;
- resolve and `FOR SHARE` lock the definition by type at `READ COMMITTED`;
- reject missing and disabled definitions with the ADR-0007 error codes;
- validate the strict request shape, UTC availability, object payload, 256 KiB
  payload limit, depth 32, and duplicate property names;
- obtain time from an injectable server clock and preserve Domain-owned UUID v7
  Job ID generation;
- create Jobs with priority 0, maximum attempts 1, and availability defaulted to
  creation time;
- persist the definition ID and type snapshot and return success only after
  commit;
- expose the ADR-0007 `201 Created` response and RFC 9457 error contract through
  a thin Client API endpoint;
- cover domain defaults/invariants, PostgreSQL persistence and disable/submit
  concurrency, and API request/response/error contracts with tests.

## Slice 1A — Observe a submitted Job

A client can retrieve an accepted Job by ID and inspect its persisted state.

Implemented:

- ADR-0009 defines the minimal read response, missing-Job error, and data
  exposure rules;
- a focused Application query and thin Client API endpoint retrieve a Job by ID;
- PostgreSQL remains the source of truth across API process restarts;
- successful submission includes a stable Location header;
- Application, PostgreSQL integration, and API integration tests cover the
  retrieval behavior and contracts.

This extends Slice 1 without changing its payload/default semantics. Execution
progress, results, filtering, and history can extend the read surface later.

## Slice 1B — Submit idempotently

A client can repeat a submission after losing the response without creating a
second Job.

Implemented:

- ADR-0010 defines opt-in `Idempotency-Key`, its temporarily global scope,
  exact payload-text equivalence, availability presence/value, conflict, and
  lifetime contracts;
- SubmitJob coordinates keyed requests within its PostgreSQL transaction and
  persists the key, request identity, and original success snapshot with the Job;
- database uniqueness and transaction advisory locks prevent concurrent keyed
  requests from creating duplicate Jobs;
- equivalent replay returns the original `201` representation and Location,
  including after API restart, definition disabling, or elapsed availability;
- conflicting reuse returns RFC 9457 `409` with `idempotency_key_conflict`;
- requests without a key preserve ADR-0007 behavior, and GET preserves ADR-0009;
- Application, PostgreSQL concurrency/rollback/constraint tests, and API
  integration tests cover the completed behavior.

Keys and identities are retained at least as long as their Jobs; no cleanup or
retention policy is implemented. Submission idempotency is independent of
automatic workload retries and does not guarantee exactly-once external effects.

Workload input/version/secret or artifact-reference extensions should receive a
separate submission slice only when a concrete scenario needs them. Preserve
ADR-0006 and explicitly evolve ADR-0007's strict request contract when necessary.

## Slice 2 — Execute a submitted Job (incomplete)

A registered Worker executes one supported Job and a client retrieves its outcome
and a small workload-specific result. Automatic retries and pause are excluded.

Decisions required before implementation:

- Worker identity/trust, registration, heartbeat, capabilities and capacity meaning
  are resolved by ADR-0011; claim eligibility, ordering, capacity reservation,
  Pending -> Running transition, initial Lease duration and claim transaction are
  resolved by ADR-0012; ADR-0013 resolves completion slot release and renewal;
  the execution resource boundary remains open;
- loss detection and finalization without automatic rerun are resolved by ADR-0014,
  preserving ADR-0013's lock order and first-committed terminal transition rule;
- client-visible outcome/result contract; ADR-0013 resolves Worker result/error
  limits, durable replay and late completion before finalization.

Tasks and completion criteria:

- implement focused registration/liveness and claim use cases through Worker API;
- atomically create an attempt and lease under the accepted concurrency rules;
- add a minimal Worker agent and bounded handler to exercise the protocol;
- commit success/failure and result before acknowledging completion;
- expose outcome through the Client API read surface;
- ensure lost execution is eventually recorded without automatic retry;
- test state invariants, concurrent claims/capacity, expiration/finalization races,
  repeated reports, and the submit-to-result path against real PostgreSQL.

The slice is incomplete until the execution path and its defined loss behavior
work. It does not require a general Workflow engine or full CEF scenario runtime.

### Slice 2A — Worker registration and liveness (implemented)

- ADR-0011 defines stable Worker identity, process-session replacement, temporary
  trusted/private deployment, strict registration, heartbeat and liveness contracts;
- Worker API PUT persists desired name, positive execution-slot capacity and a
  full ordinal supported-type set independent of definition existence/enabled state;
- same-session registration updates configuration and liveness; a different session
  atomically replaces it and fences old-session heartbeats and delayed PUTs using
  durable accepted-session history;
- Application-owned READ COMMITTED transactions and PostgreSQL advisory/row locks
  coordinate first insertion, replacement and heartbeat across API instances;
- server TimeProvider supplies UTC timestamps, LastSeenAtUtc is monotonic, and
  focused options provide 10-second heartbeat and derived 30-second offline timeout;
- migration preserves legacy Workers and adds paired nullable session fields,
  capacity check, constrained capability table and minimal accepted-session history;
- Domain, Application, PostgreSQL concurrency/rollback/constraint and API restart
  tests cover the implemented registration and liveness path.

No Jobs execute, attempts or leases are created, or capacity is reserved in 2A.
There is no Worker executable, read/list endpoint, background monitor or recovery.

### Slice 2B — Atomic claim and execution ownership (implemented)

- ADR-0012 defines the Worker claims endpoint, current-session and online
  eligibility, deterministic ordering, execution slots and session-bound Lease;
- one Application-owned READ COMMITTED transaction locks Worker, counts active
  Leases and selects a Pending, available, exactly supported Job with FOR UPDATE
  SKIP LOCKED, ordered by descending Priority then ascending availability,
  creation and native UUID;
- Job.StartAttempt transitions Pending -> Running and creates one Running attempt
  numbered from complete history; the same transaction inserts its Lease with
  current WorkerId/SessionId, a server acquisition time and default 30-second duration;
- one unreleased, unexpired Lease occupies a slot across all Worker sessions;
  capacity changes do not remove ownership, and expired leases cease to count
  without finalizing or retrying their Job/attempt;
- the thin Worker endpoint returns execution input only after commit (the original
  eight fields plus the ADR-0013 leaseToken extension), or
  identical empty 204 for no eligible work and exhausted capacity, with stable
  validation, session, missing-Worker and offline Problem Details;
- migration preserves legacy null-session leases and constrains non-null session
  format and its relationship to accepted Worker session history;
- Domain, Application, PostgreSQL concurrency/rollback/constraint and API restart
  and multi-instance tests cover ownership, fencing and capacity.

Slice 2B creates durable ownership only. Slice 2C adds renewal and reporting below;
Slice 2D adds automatic loss finalization. Workloads still do not execute.

### Slice 2C — Lease renewal and execution reporting (implemented)

- ADR-0013 extends claim with a server-generated 256-bit opaque Lease token;
  PostgreSQL stores its SHA-256 hash only, with no legacy backfill;
- current-session/token-fenced renewal extends valid ownership monotonically,
  including for an offline Worker, without changing heartbeat liveness;
- strict PUT completion accepts success with an object result up to 64 KiB/depth 32,
  or failure with bounded error code/message;
- Job coordinates Running -> Succeeded/Failed for itself and its attempt; the
  same transaction persists matching completion/release times and frees capacity;
- ReportId and original snapshot support structurally equivalent replay after
  response loss or API restart, with stable conflicts for different reports;
- focused Application READ COMMITTED transactions and PostgreSQL Worker -> Job ->
  JobAttempt -> Lease locks order renewal/reporting with registration, heartbeat
  and claim; rollback cleanup keeps the same scope reusable;
- Domain, Application, PostgreSQL constraint/migration/rollback/concurrency and
  API contract/restart/multi-instance tests cover the protocol.

Late completion is accepted after expiration only while execution remains Running
and unreleased with valid ownership. This is not lost-execution recovery. No Worker
executable, actual workload execution or client-visible outcome/result expansion
is implemented. The whole Slice 2 remains incomplete.

### Slice 2D — Lost-execution finalization (implemented)

ADR-0014 defines terminal loss, exact expiration eligibility, nonblocking ordered
row locks and the first-committed terminal transition rule. Internal finalization
of one expired Lease is implemented: Job becomes Failed, attempt becomes Abandoned,
and the Lease is released atomically without retry or a synthetic Worker report.
Domain, Application, PostgreSQL concurrency/rollback/legacy and API regression
tests cover that path.

Bounded read-only discovery and an internal sweep are also implemented, with keyset
progression, independent candidate transactions, failure isolation and revisits of
skipped work. PostgreSQL tests cover backlog, concurrency, reset and legacy migration
compatibility. The API hosted service runs an immediate first pass and waits
5 seconds after each pass, inspecting at most 100 candidates by default. Validated
configuration supports explicit disablement. Controlled host tests verify startup,
shutdown, temporary failure, restart, concurrent instances and Worker API races.
Eventual recording requires an enabled host, available database and locks that
eventually release; it has no exact deadline during outage or contention.
Slice 2 remains incomplete; the next increment is Slice 2E.

### Slice 2E — Minimal Worker and bounded test workload (in progress)

ADR-0015 accepts a long-lived capacity-one agent, stable local identity, a bounded
in-process test workload, lease maintenance, shutdown and transport failure rules.
The executable persists WorkerId, creates a fresh process session, registers and
heartbeats through HTTP. It claims one Job, executes `test.bounded-sum.v1`, renews
confirmed ownership and reports success or bounded input failure. Controlled
timers bound execution and shutdown; lease loss stops local work without a
synthetic outcome. Worker and real API/PostgreSQL tests cover execution, result
persistence, lease release, API restart and enabled-host finalization.

Bounded recovery repeats only registration, renewal and frozen completion after
recognized transport failures. It preserves identity/report data, caps attempts
and elapsed time, and never repeats an ambiguous claim or adopts an old execution.
Real API/PostgreSQL tests verify response loss after commit, completion replay
across restart/expiry, session fencing and loss after an unacknowledged renewal.
Local Aspire/process qualification remains to be implemented. Slice 2E and Slice 2
remain incomplete; the next increment is local orchestration/process qualification.

### Slice 2F — Client-visible terminal outcome and small result (proposed)

Explicitly evolve ADR-0009's read contract to expose terminal outcome and a small
result. Verify submit-to-result and guaranteed lost-execution finalization before
marking Slice 2 complete. This may be combined with 2E in a controlled end-to-end
increment, but is not implemented by 2C.

## Slice 3 — Observe and cancel a long-running scenario (proposed)

A client observes stages, counters, and available partial results, then can
request cooperative cancellation. Validate with a controlled scenario modeled on
Send Letters and a browser/API workload following ADR-0004.

Before implementation, decide progress ordering/retention, result publication,
safe-stop behavior, Pending cancellation, completion/cancellation races, and
unresponsive execution handling. Implement and test those contracts, resource
cleanup, lease maintenance during long execution, and partial-result retrieval.
Add artifact publication only with its storage/access/lifecycle contract defined.

Pause and automatic retries remain excluded.

## Later slices — Separate decisions, ordering open

- **Automatic retries:** define failure classification, policy ownership, limits,
  backoff, eligibility after loss, and external-effect reconciliation. Create new
  attempts and preserve earlier history; do not rewrite old execution outcomes.
- **Live-process pause:** decide whether to implement the current candidate, then
  define safe points, resume, cancellation while paused, and lease/resource/capacity
  accounting. Process survival is a limitation, not durable recovery.
- **Durable resume:** first establish that restart or cross-Worker continuation is
  required. Define checkpoints, input/handler compatibility, resource rebuilding,
  uncertain effects, and resume/attempt identity before choosing a schema.
- **Durable Workflow composition:** introduce only for a concrete orchestration
  requirement spanning Jobs, with durable dependencies, waits, and recovery.

## Schema evolution and product checkpoint

Add schema changes alongside the slice that needs them. Submission deduplication
and attempt completion/results are implemented. Candidate areas include artifacts, progress,
and control requests. Checkpoints and workflow state remain conditional on later decisions.
Keep Job and JobAttempt distinct; do not add a permanent one-attempt-per-Job
constraint or speculative retry/workflow tables.

Currently submission, opt-in idempotent replay, retrieval by ID, Worker
registration/liveness, atomic claim, renewal, idempotent execution reporting and
automatic loss finalization with a bounded discovery sweep are implemented.
The minimal Worker executes a bounded synthetic workload through HTTP with bounded
transport recovery. Process qualification, client-visible terminal outcome/result and control
remain absent. Complete Slice 2E, followed by 2F as described above.
A minimally useful execution product still needs process qualification,
client-visible outcomes, and a concrete workload. Long-running scenario control
follows in Slice 3; automatic retries, pause, and Workflow are not prerequisites
for the first execution slice.
