# ADR-0012: Atomic Job Claim and Execution Ownership

**Status:** Accepted

## Context

Slice 2A establishes durable Worker sessions and liveness. Slice 2B must assign
one accepted Job without duplicate ownership or exceeding Worker capacity across
independent API instances. This decision implements ownership only; Slice 2 stays
incomplete under ADR-0008. ADR-0001, ADR-0002 and ADR-0005 retain their boundaries.

## Decision

### Worker API and trust

`POST /api/worker/workers/{workerId}/claims` requires exactly one
`Worker-Session-Id` header. No request body is required or interpreted. Both
identifiers must be RFC UUID v7, with the same parsing and validation as heartbeat.
Registered capabilities and capacity supply all claim inputs. No client scheduling
inputs are accepted.

After commit, return `200 OK` with exactly:

```json
{
  "jobId": "019ec569-5a00-7000-8000-000000000001",
  "attemptId": "019ec569-5a00-7000-8000-000000000002",
  "leaseId": "019ec569-5a00-7000-8000-000000000003",
  "attemptNumber": 1,
  "type": "browser.capture-page",
  "payload": { "url": "https://example.com" },
  "acquiredAtUtc": "2026-09-06T12:00:00Z",
  "expiresAtUtc": "2026-09-06T12:00:30Z"
}
```

Payload is the submitted JSON value read from `jsonb`, not its original textual
spelling. This execution input does not change the Client API's payload exclusion.
Exclude definition internals, submission identity, Worker configuration, progress
and result fields. All timestamps are UTC with `Z`.

No eligible Job and exhausted capacity both return `204 No Content` with an empty
body. This includes unsupported and future Jobs and candidates currently locked
by another transaction. No reason for no-work is disclosed.

Errors are RFC 9457 `application/problem+json` with type, title, status and stable
code. Type is `urn:synestra:problem:` plus the code with hyphens.

| Condition (checked in this order) | Status | Code |
|---|---|---|
| Invalid worker ID or missing, repeated, malformed, non-v7 session header | 400 | invalid_request |
| Unknown Worker | 404 | worker_not_found |
| Non-current session, including legacy null session | 409 | worker_session_replaced |
| Current Worker offline | 409 | worker_offline |
| Unexpected failure | 500 | internal_error |

WorkerId, SessionId and Lease identity are fencing identifiers, not credentials.
ADR-0011's trusted/private deployment limitation remains; authentication is absent.
Claim is not idempotent: loss of a response can leave committed ownership, and a
repeat may claim another Job if capacity permits. Recovery of that lost execution
is a later increment, not an implicit retry of the original Job.

### Eligibility, time and ordering

Only a current, online Worker may claim. Offline means
`serverUtc - LastSeenAtUtc >= OfflineAfterSeconds`, including exactly 30 seconds
with the default ADR-0011 options. Claim never changes LastSeenAtUtc.

Application samples TimeProvider after acquiring the Worker row lock, matching
ADR-0011: waiting for the lock must not preserve a stale pre-wait liveness or
capacity assessment. Truncate once to PostgreSQL microseconds and use this one
instant for liveness, capacity, availability and acquisition. Lease duration comes
from focused ClaimWorkOptions, a positive whole-second value defaulting to 30.
ExpiresAtUtc equals acquisition plus duration. Eligibility is assessed at this
instant, not promised through an arbitrarily delayed response delivery.

A candidate has Pending status, AvailableAtUtc <= serverUtc, and an exact
ordinal case-sensitive type in the Worker's current supported types. SQL uses
`COLLATE "C"` equality, with no wildcard, prefix, pool or resource matching.
Do not query or lock JobDefinition. Disabling blocks new submissions only
(ADR-0007); the existing definition FK still prevents physical deletion (ADR-0006).

Select one candidate with this PostgreSQL ordering and locking:

```sql
SELECT j.* FROM jobs AS j
WHERE j.status = 'Pending' AND j.available_at_utc <= @serverUtc
  AND EXISTS (
    SELECT 1 FROM worker_supported_types AS t
    WHERE t.worker_id = @workerId AND t.type = j.type COLLATE "C")
ORDER BY j.priority DESC, j.available_at_utc ASC, j.created_at_utc ASC, j.id ASC
LIMIT 1 FOR UPDATE OF j SKIP LOCKED
```

Priority descends; availability and creation ascend; native PostgreSQL UUID order
compares the 16 UUID bytes and breaks complete ties, equivalent to ordinal order
of canonical fixed-width UUID hex. All ordering fields are non-null. Selection is
deterministic among visible, unlocked eligible rows, not a global FIFO or fairness
promise under contention. See PostgreSQL's [SELECT locking contract](https://www.postgresql.org/docs/17/sql-select.html#SQL-FOR-UPDATE-SHARE)
and [READ COMMITTED behavior](https://www.postgresql.org/docs/17/transaction-iso.html#XACT-READ-COMMITTED).

### Capacity and session ownership

Count authoritative Lease rows for WorkerId across **all** sessions, including
legacy leases: `released_at_utc IS NULL AND expires_at_utc > @serverUtc`.
Each counts as one execution slot. Expiration exactly at serverUtc frees the slot.
No mutable available-capacity column, counter, cache or reservation table exists.
An expired lease frees capacity but leaves its Running Job/attempt unchanged;
there is no finalization or automatic rerun in this increment.

Same-session registration can lower declared capacity without deleting leases.
When active count >= capacity, return no-work until slots cease to count. Session
replacement likewise leaves previous leases intact and bound to their original
sessions. Their subsequent execution/loss handling belongs to a later increment.

### Domain transition and transaction

Application owns one focused ClaimWork READ COMMITTED transaction. Persistence
implements its SQL and tracking details through IClaimWorkPersistence; no generic
repository or transaction framework is introduced.

1. Lock the existing Worker row FOR UPDATE, without a registration advisory lock.
2. Sample server time and check current session and liveness.
3. Count active leases under that Worker lock; stop if capacity is exhausted.
4. Select and lock one eligible Job using the query above; stop if absent.
5. Load its attempt history while holding the Job lock. Job.StartAttempt owns
   Pending -> Running and creates one Running JobAttempt with number
   `max(existing numbers) + 1`, or 1 for empty history. Domain rejects non-Pending
   state, non-UTC start time, start before availability and inconsistent completed
   state. Validation failure cannot partly mutate the Job.
6. Create one UUID v7 Lease for the attempt with current WorkerId and SessionId.
   Attempt.StartedAtUtc equals Lease.AcquiredAtUtc. Persist Job, attempt and lease
   atomically, commit, then return success. Job.CompletedAtUtc stays null; payload,
   definition ID and type snapshot stay unchanged.

MaxAttempts does not authorize or implement a retry engine. Claims only consume
Pending Jobs; Running, Failed, terminal or lost Jobs are never reclaimed here.
Keep unique (JobId, attempt number), and do not add one-attempt-per-Job uniqueness.
Job owns attempt creation through a focused method, without imposing broader
aggregate/repository abstractions. JobAttempt has no public creation/mutation path.

Lock order is Worker -> Job -> insert JobAttempt -> insert Lease. Registration
takes its advisory lock before the Worker lock and never takes a Job lock;
heartbeat takes only Worker. Claim never requests the advisory lock. Future lease
operations must respect Worker-before-Job ordering and must not take a Worker
lock while already holding a conflicting Job/Lease lock. FK checks reference
already locked Worker/Job and immutable accepted-session history. Different Workers
share no global lock; SKIP LOCKED avoids waiting on another candidate Job.

A replacement committed before claim's Worker check fences an old-session claim.
A claim ordered before replacement may commit successfully and retain its original
session binding. Heartbeat ordered before claim contributes its committed liveness;
heartbeat after claim does not retroactively change that decision. A first
registration still uncommitted may produce claim 404; repeat after its commit.

Disposal rolls back incomplete work and detaches the transaction's Job, loaded
attempts and new Lease even after SaveChanges or commit failure. Reusing the same
scope must re-read committed state and cannot persist rolled-back transitions.
Cancellation propagates, never becoming success or no-work.

### Schema and legacy rows

Add nullable `leases.session_id` without backfill or deletion. New domain-created
leases require non-null RFC UUID v7 WorkerId and SessionId. A database check allows
null legacy sessions and validates the UUID version/variant for non-null values.
A composite FK (worker_id, session_id) references durable worker_sessions with
restrictive deletion, so a bound Lease identifies a session actually registered
for that Worker. It deliberately references history, not the mutable current
session, allowing replacement without rewriting ownership.

Existing unique attempt/Lease relationship and Worker/Job FKs remain. No trigger
distinguishes old from new rows: direct SQL can still insert null session_id.
Required new-session binding is enforced by Domain/Application, while the database
enforces non-null binding validity and relationships. Test this limitation and
migration compatibility explicitly rather than claiming database NOT NULL safety.

## Consequences and next increment

Slice 2B establishes durable execution ownership and capacity reservation. It does
not start a workload. Slice 2C next defines current-session and lease-token fencing,
renewal, idempotent success/failure reports, a small result contract and completion
races. Lost-execution finalization may be Slice 2D. Worker executable, execution,
client-visible outcome, renewal/reporting and loss recording are still required
before Slice 2 is complete. No authentication, retry, background service, release
endpoint, polling policy, retention, progress or resource-specific schema is added.
