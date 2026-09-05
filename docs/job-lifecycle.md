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

These execution requirements are accepted, not yet implemented. See ADR-0008
and `execution-scenarios.md`.

## Proposed

Pending -> Running -> Succeeded
                   -> Failed

Running -> Cancelled

## Undecided

- Whether Job becomes Running when an attempt is created or when a lease is acquired.
- Whether an expired lease marks the attempt Failed, Abandoned or Expired.
- How a future retry policy creates a new attempt after lease expiration;
  the initial execution path does not do so automatically.
- Whether late completion after lease expiration is always rejected.
- Who decides that retry limit has been exhausted.
- Pending cancellation, cancellation/completion races, and unresponsive handlers.
- Exact loss-recording transitions and their effect on Job status.
- Pause resource/lease accounting and the relationship between resume and attempts.
