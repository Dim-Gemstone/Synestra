# ADR-0011: Worker Registration and Liveness

**Status:** Accepted

## Context

Slice 2A establishes durable Worker identity, process sessions, capabilities and
liveness through ADR-0005's Worker API. It does not execute Jobs or reserve
capacity. ADR-0001, ADR-0002 and ADR-0008 retain their boundaries.

## Decision

### Identity and trust

The agent generates and persists a stable UUID v7 WorkerId once, and generates
a UUID v7 SessionId for each process launch. Both must have the RFC UUID variant.
These are protocol identifiers and fencing values, not authentication credentials.
No authentication is implemented. The Worker API is temporarily suitable only
inside a trusted/private deployment boundary. Anyone with access can replace a
Worker session; future authentication and proof of identity remain undecided.

A registration with a different SessionId replaces the current session. UUID
timestamp ordering is not used. Concurrent registrations for one Worker serialize;
the last committed registration determines its complete desired state. Both may
succeed. Subsequent requests from the displaced session, including a delayed
registration PUT, receive a stable conflict. A durable record of accepted SessionIds
distinguishes an unseen new session from a previously replaced one without trusting
client clocks or ordering UUID timestamps. Same-session PUT remains desired state;
only an unseen SessionId can replace it. Future claim and Lease operations must
check WorkerId together with SessionId; their exact fencing,
ownership, and capacity semantics require Slice 2B decisions.

### Registration

`PUT /api/worker/workers/{workerId}/registration` accepts exactly these required,
case-sensitive JSON properties:

```json
{
  "sessionId": "019ec569-5a00-7000-8000-000000000001",
  "name": "worker-01",
  "capacity": 4,
  "supportedTypes": ["browser.capture-page"]
}
```

WorkerId and SessionId are UUID v7. Name is non-whitespace and at most 200 .NET
characters. Capacity is a positive Int32 counting maximum simultaneous execution
slots, with no reservation or release yet. Supported types contain 1–100 unique
ordinal, case-sensitive strings, each non-whitespace and at most 100 .NET
characters, matching JobDefinition.Type limits. Values are not trimmed or folded.
Duplicates, unknown/duplicate JSON properties, missing/null fields, invalid shapes
and invalid IDs are rejected. Text must be representable in PostgreSQL (no NUL).
Capabilities need no existing or enabled JobDefinition and have no definition FK.

The body is bounded to 64 KiB, including when Content-Length is absent: this fits
100 maximum-length types even with JSON Unicode escaping, plus name and metadata.
Oversized bodies and unsupported media types return `400 invalid_request`.

First and repeated PUTs return `200 OK`, without Location or a speculative GET:

```json
{
  "workerId": "019ec569-5a00-7000-8000-000000000002",
  "sessionId": "019ec569-5a00-7000-8000-000000000001",
  "name": "worker-01",
  "capacity": 4,
  "supportedTypes": ["browser.capture-page"],
  "registeredAtUtc": "2026-09-05T12:00:00Z",
  "sessionStartedAtUtc": "2026-09-05T12:00:00Z",
  "lastSeenAtUtc": "2026-09-05T12:00:00Z",
  "heartbeatIntervalSeconds": 10,
  "offlineAfterSeconds": 30
}
```

Types are sorted ordinally. Same-session PUT replaces name, capacity and the full
capability set, and confirms liveness. New-session PUT also replaces SessionId and
SessionStartedAtUtc. RegisteredAtUtc never changes; SessionStartedAtUtc changes
only on a session transition (including adoption of a legacy row). Server time is
read through TimeProvider after acquiring locks. LastSeenAtUtc is the maximum of
its previous value and that server time, including across session replacement.
SessionStartedAtUtc records the server time even if the clock moved backwards.
Timestamps use UTC and `Z`; persistence has PostgreSQL microsecond precision, so
Application truncates server timestamps to microseconds before domain mutation
and response mapping. Desired-state idempotency does not freeze liveness timestamps.

### Heartbeat and liveness

`POST /api/worker/workers/{workerId}/heartbeat` uses exactly one
`Worker-Session-Id` header with a UUID v7. A successful current-session heartbeat
atomically advances LastSeenAtUtc monotonically and returns `204 No Content`.
It cannot change configuration or capabilities. There is no heartbeat body contract.
Validate both identifiers before looking up the Worker.

Focused Application options supply a 10-second heartbeat interval and 30-second
offline threshold, also returned by registration. Offline is derived when
`serverUtc - LastSeenAtUtc >= offlineAfterSeconds`, including the exact boundary;
an earlier clock value is online. No persisted status flag, worker read/list
endpoint, background monitor or lost-execution action is introduced.

Errors use RFC 9457 with type, title, status and stable code, consistent with the
Client API. Types are `urn:synestra:problem:` plus the code with hyphens:

| Condition | Status | Code |
|---|---|---|
| Invalid request, malformed/non-v7 ID, absent/multiple session header | 400 | invalid_request |
| Heartbeat for unknown Worker | 404 | worker_not_found |
| Heartbeat with non-current session, including a legacy null session | 409 | worker_session_replaced |
| Registration reusing a previously replaced session | 409 | worker_session_replaced |
| Unexpected failure | 500 | internal_error |

### Transactions and concurrency

Application owns one READ COMMITTED transaction per registration:

1. Acquire `pg_advisory_xact_lock(bigint)` using the first eight bytes of SHA-256
   of UTF-8 `synestra:register-worker:` plus WorkerId's canonical lowercase `D`
   representation, interpreted as a signed big-endian Int64.
2. In a separate statement after that lock, read the currently committed Worker
   using `SELECT ... FOR UPDATE`, then its capabilities while holding the row lock.
3. For a different SessionId, check durable accepted-session history. Reject a
   previously accepted ID with `409 worker_session_replaced`. Otherwise insert
   its accepted-session record in this same transaction.
4. Create or apply the domain desired-state update; persist Worker, session history
   and capability changes together. Commit before success. Disposal rolls back failure.

The advisory lock covers absence and first insertion, with Worker primary-key
uniqueness as the final database guard. Different IDs do not globally serialize;
a hash collision can only cause extra serialization, never identity confusion.
Full UUIDs always identify rows. Locks release on transaction end/connection loss.

Heartbeat uses an Application-owned READ COMMITTED transaction and the same
Worker row `FOR UPDATE` lock, checks the current session in Domain, updates only
liveness, and commits. A heartbeat ordered before replacement can succeed; one
ordered after it conflicts without changing state. An unknown-worker heartbeat
may return 404 while the first registration is uncommitted. It can retry after
registration completes. There are no in-memory locks, caches or process-local
coordination state. Domain knows neither locks nor HTTP error codes.

### Schema and legacy compatibility

Workers gain nullable session_id and session_started_at_utc, constrained to be
both null or both non-null, and a capacity > 0 check. Existing rows are neither
deleted nor backfilled. A legacy row cannot heartbeat until registration adopts
it, preserving RegisteredAtUtc.

worker_supported_types has a composite (worker_id, type) primary key, an ordinal
`C`-collated type of maximum length 100, a practical database non-whitespace check,
and a cascade-delete Worker FK. There is no JobDefinition FK. Domain enforces
Unicode whitespace and collection-count limits; PostgreSQL additionally protects
capacity, paired session fields, capability length, uniqueness and relationship
integrity.

Additionally, worker_sessions stores only (worker_id, session_id), with a composite
primary key and cascade-delete Worker FK. This persistence-only protocol history
is necessary to reject delayed registration from a replaced session: the current
session fields alone cannot distinguish it from an unseen new process. History
survives API restart and is retained as long as the Worker; no cleanup is added.
This table contains neither authentication material nor execution/Lease history.
Failed or rolled-back registrations do not consume SessionIds. Legacy Workers
start without session history and gain their first record when adopted.

## Consequences and next increment

Worker registration and liveness survive independent API instances and restarts.
This completes only Slice 2A. Slice 2 remains incomplete: Slice 2B next defines
atomic claim, JobAttempt creation, session-bound Lease and capacity reservation.
Worker executable, workload execution, renewal, completion/results, loss recovery,
automatic retries, authentication, pools and resource-specific accounting remain
outside this increment.
