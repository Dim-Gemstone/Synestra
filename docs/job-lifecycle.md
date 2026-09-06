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

Workload execution, reporting and control remain accepted requirements without
implementation. Claim ownership is implemented as described below. See ADR-0008,
ADR-0012 and `execution-scenarios.md`.

## Proposed

Running -> Succeeded
        -> Failed

Running -> Cancelled

## Undecided

- Whether an expired lease marks the attempt Failed, Abandoned or Expired.
- How a future retry policy creates a new attempt after lease expiration;
  the initial execution path does not do so automatically.
- Whether late completion after lease expiration is always rejected.
- Who decides that retry limit has been exhausted.
- Pending cancellation, cancellation/completion races, and unresponsive handlers.
- Exact loss-recording transitions and their effect on Job status.
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
existing ownership unchanged. Finalization, release, renewal and late reporting are
later increments. Claims never create retries or reclaim Running/Failed/lost Jobs;
MaxAttempts is not an automatic retry engine. Slice 2 remains incomplete.
