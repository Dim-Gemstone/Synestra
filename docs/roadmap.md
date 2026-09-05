# Roadmap

Slice 1 is implemented. ADR-0008 accepts the execution direction and the
requirements illustrated in `execution-scenarios.md`. The later slices below are
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

## Slice 1A — Observe a submitted Job (proposed next slice)

A client can retrieve an accepted Job by ID and inspect its persisted state.

Tasks and completion criteria:

- define a minimal read response, missing-Job error, and data exposure rules;
- implement a focused Application query and thin Client API endpoint;
- verify retrieval after submission and after API restart against PostgreSQL;
- add a stable Location header to submission once the read endpoint exists;
- cover API contracts and persistence with tests.

This extends Slice 1 without changing its payload/default semantics. Execution
progress, results, filtering, and history can extend the read surface later.

## Slice 1B — Submit idempotently (proposed)

A client can repeat a submission after losing the response without creating a
second Job.

Before implementation, decide key scope/ownership, request equivalence,
conflicting reuse, retention, and response replay. Implement transactional
deduplication with PostgreSQL concurrency tests and API tests. This is independent
of automatic workload retries and does not guarantee exactly-once external effects.

Workload input/version/secret or artifact-reference extensions should receive a
separate submission slice only when a concrete scenario needs them. Preserve
ADR-0006 and explicitly evolve ADR-0007's strict request contract when necessary.

## Slice 2 — Execute a submitted Job (proposed)

A registered Worker executes one supported Job and a client retrieves its outcome
and a small workload-specific result. Automatic retries and pause are excluded.

Decisions required before implementation:

- Worker identity/trust, registration, heartbeat, supported workload routing,
  capacity reservation/release, and execution resource boundary;
- atomic claim algorithm and transaction boundaries, availability ordering,
  Job/attempt transitions, lease duration/maintenance, and loss detection;
- lost-execution finalization without automatic rerun, late reports, duplicate
  completion requests, and completion/expiration races;
- result/error contracts, limits, and rules for selecting the current Job outcome.

Tasks and completion criteria:

- implement focused registration/liveness and claim use cases through Worker API;
- atomically create an attempt and lease under the accepted concurrency rules;
- add a minimal Worker agent and bounded handler to exercise the protocol;
- commit success/failure and result before acknowledging completion;
- expose outcome through the Client API read surface;
- ensure lost execution is eventually recorded without automatic retry;
- test state invariants, concurrent claims/capacity, expiration/finalization races,
  repeated reports, and the submit-to-result path against real PostgreSQL.

Registration and heartbeat may be separate implementation PRs within this slice.
The slice is incomplete until the execution path and its defined loss behavior
work. It does not require a general Workflow engine or full CEF scenario runtime.

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

Add schema changes alongside the slice that needs them. Candidate areas include
submission deduplication, attempt results, artifacts, progress, and control
requests. Checkpoints and workflow state remain conditional on later decisions.
Keep Job and JobAttempt distinct; do not add a permanent one-attempt-per-Job
constraint or speculative retry/workflow tables.

Currently submission is implemented; execution, results, and control are not.
The proposed next increment is Slice 1A, while defining the Slice 2 protocol is
the next architectural step. A minimally useful execution product still needs
Worker execution, reliable ownership/loss handling, client-visible outcomes, and
a concrete workload. Long-running scenario control follows in Slice 3; automatic
retries, pause, and Workflow are not prerequisites for the first execution slice.
