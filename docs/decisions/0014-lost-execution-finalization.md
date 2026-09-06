# ADR-0014: Lost-Execution Finalization Without Retry

**Status:** Accepted

## Context and implementation boundary

ADR-0008 requires eventual recording of lost execution without automatic rerun.
ADR-0012 stops counting expired Leases toward capacity; ADR-0013 permits late
completion until another terminal transition commits. This decision defines all
of Slice 2D while implementation proceeds only through explicitly selected units.
Slice 2D and Slice 2 remain incomplete until the automatic host path is verified.
No Worker executable, workload execution or Client result extension is included.

## Decision

### Eligibility and meaning of loss

The internal FinalizeExpiredExecution use case accepts a LeaseId. After all row
locks, it requires a consistent Worker/Job/JobAttempt/Lease relationship, Running
and incomplete Job and attempt, an unreleased Lease with ExpiresAtUtc <= serverUtc,
no persisted completion ReportId or snapshot, and no result/error or other terminal
outcome. Exact expiration is eligible, without an additional grace period.

Offline status alone is insufficient. Session replacement does not end execution
early: its old Leases become eligible only at expiration. This is a server operation,
so neither current Worker session nor Lease token is required. Legacy null sessions
and token hashes are eligible. Worker still participates in lock ordering, but its
liveness, configuration and registration/session history remain untouched. Do not
apply Worker API ownership validation to this use case. Missing rows are skipped;
inconsistent or orphan legacy relationships/chronology receive a safe diagnostic
and no lifecycle repair. Diagnostics contain identifiers/reason codes only, never
payload, result, error contents, tokens, hashes or completion snapshots.

Loss finalization records absence of a committed completion. It does not prove
absence of external workload effects, stop the Worker process or provide exactly-once
execution. Repeating a failed control-plane transaction does not rerun a workload.

### Terminal transition and time

Job coordinates its attempt through a focused AbandonAttempt method, analogous to
SucceedAttempt/FailAttempt. Domain validates UTC, chronology, attempt membership,
Running/incomplete state and absence of result/error before any mutation. Invalid
or repeated transitions cannot partly mutate the pair or overwrite terminal state.
Domain contains no HTTP, EF, options, protocol identity or response DTO concerns.

One transaction records:

- JobAttempt Running -> Abandoned and Job Running -> Failed;
- JobAttempt.FinishedAtUtc, Job.CompletedAtUtc and Lease.ReleasedAtUtc equal to
  the same serverUtc;
- null Result, error_code `execution_lease_expired`, and error_message
  `Execution lease expired before completion was recorded.`;
- null completion_report_id and completion_snapshot, without a synthetic report.

Sample TimeProvider after all locks and truncate UTC to PostgreSQL microseconds
using the existing Application convention. This is the actual terminal decision
time, not the historical expiration. Preserve acquisition, attempt start and
expiration timestamps. Do not create an attempt or Lease, change MaxAttempts,
availability or submission identity, or return the Job to Pending. No automatic
or manual workload retry is implemented.

### Transactions and concurrency

Application owns one focused READ COMMITTED transaction per execution through
IFinalizeExpiredExecutionPersistence. Persistence owns SQL and tracking. No generic
LeaseRepository, UnitOfWork, mediator or CQRS infrastructure is introduced.

1. Read-only Lease locator obtains WorkerId, JobId and AttemptId without row locks.
2. Acquire Worker FOR UPDATE SKIP LOCKED.
3. Acquire Job FOR UPDATE SKIP LOCKED.
4. Acquire JobAttempt FOR UPDATE SKIP LOCKED.
5. Acquire Lease FOR UPDATE SKIP LOCKED.
6. Revalidate locator relationships and eligibility against the locked rows.
7. Perform Domain mutation, SaveChanges and commit before reporting Finalized.

If any required row is busy, end the transaction without mutation and return an
internal Busy/skipped result for later retry. Read-only acquisition probes may
distinguish a missing row from a skipped row; they do not authorize mutation.
Never acquire a Lease/Job/attempt lock before Worker. Existing Worker API operations
retain their blocking row-lock behavior and exact ADR-0013 error precedence.
Different Workers have no shared global lock or process-local ownership cache.

The first committed terminal transition wins. Completion first preserves its
result and persisted replay snapshot. Finalizer first causes ownership-valid
completion to return `409 attempt_already_finalized`; stale session and ownership
errors still take precedence. Finalized execution cannot renew (`lease_not_active`
after ownership checks). Renewal first requires rechecking its current expiration.
Two finalizers commit at most one terminal mutation. Failed transactions/cancellation
never become Finalized. Disposal rolls back unfinished transactions and detaches
their tracked entities, including successful SaveChanges followed by commit failure,
so the same scope can safely reread and retry committed state.

Expired Leases already stopped consuming capacity under ADR-0012. Finalization
does not release a slot twice or maintain a mutable capacity counter. A new claim
after expiration is independent of finalizing the old attempt.

### Schema and enforcement boundary

The one-execution path uses existing status/error/time fields and primary-key
lookups; it requires no schema change or new index. Existing FK relationships,
unique attempt/Lease and (JobId, number), nullable session/hash constraints and
Worker completion checks remain. No trigger, permanent one-attempt-per-Job
constraint or Worker ReportId requirement for Abandoned is added.

Domain enforces new terminal mutation rules. Application enforces eligibility,
common terminal timestamps and absence of protocol completion data. Persistence
enforces ordered locking, relationship revalidation and atomic storage. PostgreSQL
enforces existing local checks and FKs, not cross-row lifecycle consistency or
immutability against arbitrary SQL. Reportless legacy rows remain permitted;
there is no backfill or repair. Discovery indexes require evidence from its actual
query, and any necessary schema change must use a generated, tested migration.

### Bounded discovery and automatic execution (subsequent units)

One read-only sweep selects expired eligible candidates ordered by ExpiresAtUtc ASC,
Lease.Id ASC, with a fixed discovery cutoff per pass, bounded selection/memory and
at most the configured number of inspected candidates. Keyset progression uses
that pair without OFFSET. Advance the cursor through busy and failed candidates;
reset it at the end of traversal so skipped work is revisited. The cursor is only
traversal state, never correctness or ownership state. Restart/reset rediscovers
persisted work. Each candidate invokes its own FinalizeExpiredExecution transaction
and revalidates after locks. Distinguish finalized, skipped and failed outcomes;
one candidate failure preserves preceding commits and does not block following
candidates. Unexpected failures receive safe diagnostics; cancellation propagates.

The completed slice adds one thin hosted service to the current API host. Defaults:
enabled, immediate first pass after host startup, 5 seconds from the end of a pass
to the next pass, and 100 inspected candidates per pass. Validate options at startup
and allow explicit disablement. Disabled deployments do not promise eventual
finalization. The host creates one service scope per pass, calls Application, has
no overlapping local passes, propagates shutdown cancellation and waits the interval
after temporary failures. Time/timer is injectable. No transaction spans candidates
or waits. Multiple instances run safely without leader election or global locks.

Eventual finalization applies to eligible, consistent executions while an enabled
host operates, the database is available and locks eventually release. There is no
exact deadline during outages or contention. Host tests use controlled clocks and
coordination gates; unrelated API factories explicitly disable background work.

### API and scope exclusions

No Client, Worker or admin endpoint is added. ADR-0009 GetJob retains exactly its
existing response shape; status may become Failed without result/error/history
fields. ADR-0013 renewal/completion and replay contracts remain authoritative.
No Worker handler/executable, workload execution, retry, cancellation/pause,
artifacts/progress/partial results, authentication/JWT/API keys, scheduler framework,
broker/cache, cleanup/retention or automatic startup migrations are included.

## Implementation status and next increment

This ADR was recorded before production implementation. Unit 2D.1 implements atomic
finalization of one Lease through an internal use case, with Domain/Application,
real PostgreSQL concurrency/rollback/legacy and Worker API regression coverage.
The schema remains unchanged. Bounded discovery (2D.2) and hosted execution (2D.3)
remain unimplemented and require separate batches.
Automatic eventual finalization is not implemented by 2D.1 alone. After 2D.3,
Slice 2D may be marked implemented, while Slice 2 remains incomplete. The next
product increment is Slice 2E (minimal Worker and bounded workload); client-visible
terminal outcome/result remains Slice 2F.
