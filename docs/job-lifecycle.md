# Job Lifecycle

## Confirmed

- One Job can have multiple JobAttempts.
- An attempt represents one execution try.
- A Lease grants a Worker temporary ownership of an attempt.
- A worker can disappear before completing an attempt.
- Lost execution must eventually be recorded. The initial execution path does
  not automatically create another attempt; automatic retries are deferred by
  ADR-0008. Future recovery policy remains open.
- A Job may contain a whole scenario with stages and loops. A loop iteration is
  not a JobAttempt.
- Running cancellation is cooperative: request acceptance does not mean the
  execution has stopped, and completed external effects are not undone.
- Available partial results can be preserved on cancellation.
- Pause is outside the first execution slice. Live-process pause is a candidate;
  durable resume after process loss remains undecided.

Workload execution and control remain accepted requirements without implementation.
Claim, renewal and completion reporting are implemented below. See ADR-0008,
ADR-0012, ADR-0013, ADR-0014 and `execution-scenarios.md`.

## Proposed

Running -> Cancelled

## Undecided

- How a future retry policy creates a new attempt after lease expiration;
  the initial execution path does not do so automatically.
- Who decides that retry limit has been exhausted.
- Pending cancellation, cancellation/completion races, and unresponsive handlers.
- Pause resource/lease accounting and the relationship between resume and attempts.

## Implemented claim transition (ADR-0012, Slice 2B)

Pending -> Running is atomic with creating one Running JobAttempt and its Lease.
The current, online Worker session must support the exact type and have a free
execution slot; the Job must be available at server UTC. Job.StartAttempt rejects
non-Pending state, non-UTC or premature start time, and inconsistent completed state.
It owns attempt creation with max(existing attempt numbers) + 1, initially 1.

JobAttempt.StartedAtUtc equals Lease.AcquiredAtUtc. The Lease stores WorkerId and
current SessionId and expires after the configured duration, initially 30 seconds.
Job.CompletedAtUtc stays null, and payload, definition identity and type snapshot
do not change. Ownership is persisted before the Worker receives claim success;
this does not yet execute a workload or produce an execution result.

Each unreleased Lease whose expiration is strictly later than server UTC reserves
one Worker slot, regardless of its session. Expiration exactly at server time frees
capacity but leaves Job and JobAttempt Running. Session replacement likewise leaves
existing ownership unchanged. ADR-0013 adds renewal, completion release and late
reporting below. Claims never create retries or reclaim Running/Failed/lost Jobs;
MaxAttempts is not an automatic retry engine. Slice 2 remains incomplete.

## Implemented renewal and completion (ADR-0013, Slice 2C)

Claim additionally returns one opaque random Lease token. Renewal/reporting require
the current Worker process session, matching Lease Worker/session and token hash.
Legacy tokenless leases cannot use either operation. WorkerId, SessionId and LeaseId
remain identifiers; the token is a separate per-Lease fencing secret.

Lease.Renew requires an unreleased Lease with ExpiresAtUtc > serverUtc and a Running
Job/attempt. It sets max(previous expiration, serverUtc + configured duration),
preserving acquisition/start times. Renewal does not confirm Worker liveness, so
an offline Worker may renew existing valid ownership but cannot claim new work.

Job.SucceedAttempt and Job.FailAttempt coordinate Running -> Succeeded/Failed for
Job and JobAttempt. Completion validates all state/data before mutation. Success
stores an opaque result, with no error; failure stores error code/message, with no
result. FinishedAtUtc and CompletedAtUtc match the same server UTC, and Lease.Release
sets ReleasedAtUtc to that time without changing expiration. All three entities,
ReportId and original completion snapshot commit atomically. Invalid or repeated
Domain mutation cannot overwrite terminal state or partly mutate the pair.

Application supports PUT replay with the same ReportId and structurally equivalent
data, returning the original snapshot without changing timestamps. A changed report
conflicts, and replacement sessions fence replay as well. Domain does not implement
idempotency or know HTTP headers/error codes.

Expiration alone leaves execution Running in 2C. A valid late completion can still
be accepted before release/finalization; it never renews the Lease or restores past
capacity. This is network-delay tolerance, not lost-execution recovery. Slice 2D
adds loss finalization below using Worker -> Job -> attempt -> Lease locks;
the first committed terminal transition wins.
Actual workload execution and client-visible outcome/result remain later increments.

## Implemented one-execution loss transition (ADR-0014, unit 2D.1)

FinalizeExpiredExecution internally accepts one LeaseId. It takes an Application-owned
READ COMMITTED transaction, locates the execution without locks, then acquires Worker,
Job, JobAttempt and Lease FOR UPDATE SKIP LOCKED in that order. Busy rows cause a
skip and transaction disposal; each later invocation rechecks current persisted state.
No current session, token or liveness eligibility is required. Offline status and
session replacement alone do not finalize execution before expiration.

After locks, relationships must still match, Job and attempt must be Running and
incomplete without any result/error or completion ReportId/snapshot, and Lease must
be unreleased with ExpiresAtUtc <= serverUtc. Exact expiration is eligible without
grace. Legacy null-session/tokenless Leases may qualify; inconsistent/orphan rows
are skipped with safe diagnostics, without inventing repair behavior.

Job.AbandonAttempt validates UTC, chronology, membership and allowed state before
mutation. It sets Job Failed and attempt Abandoned, with null Result and the error
`execution_lease_expired` / `Execution lease expired before completion was recorded.`
FinishedAtUtc, CompletedAtUtc and ReleasedAtUtc all use the same actual server
decision time after locks, truncated to microseconds. Acquisition, start and
expiration are preserved. No ReportId or completion snapshot is invented. All
three rows commit together; rollback clears tracking even after a successful save
followed by commit failure. Repeated finalization does not write terminal rows again.

Completion first preserves its replay snapshot. Finalization first yields
`attempt_already_finalized` for ownership-valid completion and `lease_not_active`
for renewal, with all earlier ADR-0013 session/ownership errors unchanged. Renewal
first makes the finalizer check its current expiration. Worker liveness/history and
new claims are unaffected. Expired ownership already ceased to count toward capacity.

This records loss without retry, Worker termination or certainty about external
effects. No new attempt/Lease is created and no Job returns to Pending. Discovery
and automatic hosted invocation are absent, so eventual finalization is not yet
implemented. Slice 2D and Slice 2 remain incomplete.
