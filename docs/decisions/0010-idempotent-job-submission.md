# ADR-0010: Idempotent Job Submission

**Status:** Accepted

## Context

Slice 1B lets a client repeat SubmitJob after losing its response without
creating another Job. ADR-0007 leaves idempotency open; ADR-0009 provides the
stable Job location. PostgreSQL remains authoritative under ADR-0002, and the
Application owns the submission transaction under ADR-0001 and ADR-0007.

## Decision

### Opt-in key and scope

`POST /api/client/jobs` accepts an optional `Idempotency-Key` header. Without
it, every successful request creates a new Job with the existing ADR-0007
validation, defaults, definition locking, response, and error semantics.

The header must contain exactly one value of 1–128 visible ASCII characters
(`!` through `~`), excluding comma. Whitespace, control characters, non-ASCII
characters, empty values, repeated values, and comma-separated lists are invalid
(`400`, `invalid_request`). This explicit portable HTTP alphabet avoids header
normalization and coalescing ambiguity. The application does not trim, unquote,
or case-fold keys; comparisons are ordinal and case-sensitive. Clients should
generate a fresh unpredictable key for each intended submission.

The scope is temporarily global across SubmitJob in one control-plane database,
including different workload types and clients. Authentication and tenancy do
not exist yet. This is a limitation, not a decision about future principal or
tenant identity. Introducing either requires revisiting this scope explicitly.

### Request identity

Equivalence compares all of:

- `type`: exact decoded string, with ordinal case-sensitive comparison;
- `payload`: exact received JSON value text (and therefore its valid UTF-8
  representation), before conversion to PostgreSQL `jsonb`;
- `availableAtUtc`: both presence and the parsed UTC value at .NET timestamp
  precision (100 ns). Omission differs from any explicit value, even if that
  value equals the first Job's default availability. Explicit null stays invalid.

Whitespace, property order, escaping, and numeric spelling inside payload are
significant. JSON canonicalization is not introduced. Outer request whitespace,
property order, and equivalent UTC timestamp spellings are not significant.

The durable identity stores payload text as a JSON **string** within an identity
document, not as an embedded JSON object. Thus `jsonb` cannot normalize the
payload's spelling. Comparison uses the deserialized Application value record,
not `jsonb` equality or a request hash.

### Replay, conflict, and validation order

All requests first pass the existing structural validation, payload limits,
UTC-offset validation, and key validation. For a valid keyed request, the
transaction locks the key and looks up its committed submission before applying
time-dependent availability or definition eligibility checks.

An equivalent replay returns the original seven-field success representation,
`201 Created`, and the same `/api/client/jobs/{id}` Location. It does not create
a Job, consult current definition eligibility, or revalidate availability against
the current clock. A disabled definition or elapsed availability therefore does
not invalidate a previously accepted request. GET continues to return current
persisted Job state under ADR-0009.

The original submission representation is stored separately from mutable Job
state and excludes payload. Its UTC timestamp strings retain .NET precision;
PostgreSQL Job timestamp columns have microsecond precision. Storing the original
representation avoids changing ADR-0007 timestamps or losing precision on replay.

A valid request reusing a committed key with a different identity returns RFC
9457 Problem Details: `409 Conflict`, type
`urn:synestra:problem:idempotency-key-conflict`, and stable code
`idempotency_key_conflict`. No payload or prior request identity is disclosed.
An unused key still follows ADR-0007 for past availability, missing definitions,
and disabled definitions. Failures do not reserve keys or cache error responses.

### Transaction and durable storage

Extend the focused `ISubmitJobTransaction` boundary; do not introduce a generic
repository or another transaction owner. One `READ COMMITTED` transaction:

1. For keyed requests, acquire a PostgreSQL transaction advisory lock using the
   first eight bytes of SHA-256 of UTF-8 `synestra:submit-job:` plus the exact key,
   interpreted as a signed big-endian 64-bit integer.
2. In a separate statement after acquiring the lock, read the committed record
   for the full key. Return its success snapshot or the conflict outcome.
3. For new submissions, apply availability validation and resolve the definition
   under the existing `FOR SHARE` lock; reject missing or disabled definitions.
4. Create the Domain-owned UUID v7 Job. For keyed submissions, insert its key,
   request identity, and success snapshot in the same transaction as the Job.
5. Commit before returning a new success. Disposal rolls back unfinished work.

`job_submissions` is a persistence-only table with a case-sensitive primary key
on the full key, a unique non-null Job foreign key, and required request identity
and response documents. The foreign key restricts deletion of referenced Jobs.
No provisional record or incomplete reservation is committed. Database primary
key uniqueness protects against duplicate keys even if an insert bypasses the
advisory-lock protocol; its surrounding transaction must then roll back.

All keyed submission paths acquire the key lock before the definition lock.
The next statement at `READ COMMITTED` sees the preceding winner's commit.
Identical concurrent requests all return its success; conflicting concurrent
requests produce one accepted identity and conflicts for the others. Which
caller acquires the lock first is unspecified. If it rolls back, a waiting
request may create the Job. A lock-hash collision only serializes different
keys; full identities and keys are always compared independently.

PostgreSQL releases transaction advisory locks on commit or rollback, including
connection loss. There are no process-local locks, deduplication caches, or
durable pending states to clean up. See PostgreSQL's
[advisory lock documentation](https://www.postgresql.org/docs/17/explicit-locking.html#ADVISORY-LOCKS)
and [timestamp precision](https://www.postgresql.org/docs/17/datatype-datetime.html).

### Lifetime and boundaries

The key, identity, and success snapshot are retained at least as long as the Job.
There is no expiration, cleanup process, or deletion API in this slice. A future
retention decision must preserve this relationship and explicitly define key
reuse after deletion. Request identity contains another copy of opaque input
and must receive the same storage protections as Job payload.

## Consequences

- Submission can be retried across independent API instances and restarts.
- A small additional table and PostgreSQL-specific lock protocol suffice;
  Domain entities, JobAttempt, Lease, and Worker workflows are unchanged.
- Replays briefly serialize for one key; unrelated keys remain independent
  except for possible lock-hash collisions.
- The identity and snapshot add durable storage cost to opt-in submissions.
- Stable submission replay is distinct from GET's current-state representation.

## Not Decided Here

Authentication, tenant identity, retention policy, execution retries, results,
progress/history, worker execution, and exactly-once external workload effects
remain outside this decision. ADR-0006, ADR-0007, ADR-0008, and ADR-0009 otherwise
remain authoritative.
