# Job Lifecycle

## Confirmed

- One Job can have multiple JobAttempts.
- An attempt represents one execution try.
- A Lease grants a Worker temporary ownership of an attempt.
- A worker can disappear before completing an attempt.
- Expired leases must eventually allow recovery of the work.

## Proposed

Pending -> Running -> Succeeded
                   -> Failed

Running -> Cancelled

## Undecided

- Whether Job becomes Running when an attempt is created or when a lease is acquired.
- Whether an expired lease marks the attempt Failed, Abandoned or Expired.
- Whether a new attempt is created immediately after lease expiration.
- Whether late completion after lease expiration is always rejected.
- Who decides that retry limit has been exhausted.
- Whether cancellation of running work is cooperative.
