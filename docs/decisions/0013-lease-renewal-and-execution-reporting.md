# ADR-0013: Lease Renewal and Execution Reporting

**Status:** Accepted

## Context and scope

Slice 2C implements the control-plane execution protocol. It extends ADR-0012
only by adding `leaseToken` to its successful eight-field claim response.
LeaseId remains an identifier; WorkerId and SessionId remain identifiers/fencing
values. The token is a separate opaque fencing secret for that Lease. All other
claim semantics remain intact. ADR-0001/0002/0005/0008 retain their boundaries.
Slice 2 stays incomplete: no Worker executable or workload handler is added.

## Decision

### Token and trust

Application generates 32 cryptographically random bytes (256 bits) per successful
claim using RandomNumberGenerator. The transport token is exactly 43 characters
of canonical unpadded base64url. SHA-256 hashes the decoded bytes; PostgreSQL stores
only the 32-byte hash. Plaintext is returned once in that claim's committed response.
Workers cannot choose the token: a Lease-Token claim header is invalid; the claim
body remains uninterpreted and cannot supply a token. Claim is still non-idempotent.
Never log tokens/hashes or include them in diagnostics or Problem Details.
Application uses CryptographicOperations.FixedTimeEquals for hash comparison.

Token hashing and completion identity are protocol concerns outside Domain.
LeaseId is never a credential substitute. Legacy null hashes are not backfilled
and cannot renew/report. A wrong Worker/session/token or null hash all return
`409 lease_ownership_lost` after confirming Lease existence. No authentication,
API keys or JWT are added. ADR-0011's trusted/private deployment limitation remains;
this token does not authenticate registration or prevent session replacement.

### Transport and exact error precedence

Both endpoints require exactly one Worker-Session-Id and one Lease-Token:

- `POST /api/worker/workers/{workerId}/leases/{leaseId}/renewal`;
- `PUT /api/worker/workers/{workerId}/leases/{leaseId}/completion`.

WorkerId, leaseId, sessionId and reportId require RFC UUID v7. Follow existing Guid
parsing conventions, then validate version and variant. Tokens permit no padding,
whitespace, repeated/list values, alternative alphabet or nonzero unused bits.
Renewal requires no body and does not interpret it. Completion requires JSON
content type, valid UTF-8 and at most 68 KiB total body, bounded during reading
even without Content-Length. Oversize/media/shape/identifier errors are all
`400 invalid_request`. Required strings must decode as valid Unicode.

Every failure uses RFC 9457 application/problem+json with type, title, status and
stable code. Type is `urn:synestra:problem:` plus the code with hyphens. Errors
never echo request values or ownership details. Validation order is:

| Order | Condition | Status / code |
|---|---|---|
| 1 | Invalid transport, IDs, headers or completion data | 400 invalid_request |
| 2 | Unknown Worker | 404 worker_not_found |
| 3 | Legacy null or non-current Worker session | 409 worker_session_replaced |
| 4 | Unknown Lease | 404 lease_not_found |
| 5 | Lease Worker/session/token or locked relationship mismatch | 409 lease_ownership_lost |
| 6a renewal | Released Lease or non-Running/inconsistent Job or attempt | 409 lease_not_active |
| 6b renewal | Expiration <= serverUtc | 409 lease_expired |
| 6 completion | Finalized/released/non-Running execution without a stored report | 409 attempt_already_finalized |
| 7 completion | Stored report has another ReportId | 409 attempt_already_finalized |
| 7 completion | Same ReportId, different outcome/result/error | 409 completion_report_conflict |
| 7 completion | Same ReportId, equivalent data | Original 200 snapshot after commit |
| 8 | New valid operation | Mutate, save, commit, then 200 |
| any | Unexpected failure | 500 internal_error |

A stored completion snapshot identifies a terminal execution eligible for replay;
terminal state must not reject its own replay. Ownership and current-session
validation always precede replay, including after expiration or restart.
Cancellation propagates at every async boundary and never becomes an outcome.

### Renewal and time

After all locks, sample TimeProvider UTC, truncated to PostgreSQL microseconds as
in ADR-0011/0012. Worker session and Lease ownership/token must match; Job/attempt
must be Running and incomplete, and Lease unreleased with ExpiresAtUtc > serverUtc.
Exactly at expiration is too late. Domain Lease.Renew validates UTC, time no earlier
than acquisition, positive duration and active state, then assigns
`max(previous expiration, serverUtc + configured duration)`. Use ClaimWorkOptions'
same duration as claim, default 30 seconds. Preserve acquisition and attempt start.
No new Lease/attempt is created; released, expired or finalized ownership cannot revive.
Clock regression before acquisition is an unexpected server-clock failure that
rolls back; it never yields partial mutation.

Return 200 with exactly leaseId and expiresAtUtc (UTC Z), after commit. Renewal
does not change LastSeenAtUtc. Execution ownership and availability for new claims
are different signals: only registration/heartbeat establish Worker liveness.
An offline Worker may renew valid ownership, but still cannot claim new work.

### Strict completion data

The body has exactly reportId, outcome, and result for success or error for failure.
Outcome permits only exact lowercase `succeeded` and `failed`. Unknown, duplicate,
incorrectly cased, missing or null required properties are invalid. Success requires
result and forbids error; failure requires error and forbids result.

Result is a non-null JSON object, at most 64 KiB in its received UTF-8 value
representation, including internal whitespace/escapes, and depth at most 32 with
its root counted as depth 1. Reject duplicate decoded property names in all nested
objects, including inside arrays. Names/strings must contain valid Unicode and no
NUL, and numbers must fit PostgreSQL jsonb's numeric representation. These storage
constraints are checked before a transaction, with no workload-specific validation.
Error has exactly code and message: required non-whitespace strings, no NUL, at
most 100 and 2000 .NET UTF-16 characters respectively. Do not trim or case-fold.
No progress, partial results or artifacts are accepted.

### Atomic completion and Domain

Job coordinates completion through focused SucceedAttempt/FailAttempt methods,
analogous to StartAttempt. Validate before mutation: both entities Running and
incomplete, attempt belonging to Job, UTC completion no earlier than creation/start.
Repeated Domain mutation throws; replay belongs in Application. Domain stores an
opaque result string, not a transport DTO; Application enforces its JSON contract.

Success changes Job/attempt to Succeeded, persists result on attempt and leaves
error fields null. Failure changes both to Failed, persists error code/message and
leaves result null. FinishedAtUtc and Job.CompletedAtUtc use the same server instant.
Lease.Release validates UTC, time no earlier than acquisition and no prior release;
ReleasedAtUtc uses that same instant without changing ExpiresAtUtc or AcquiredAtUtc.
Persist ReportId and completion snapshot atomically with all three entities.
The commit releases the slot under ADR-0012's active Lease count.

### Idempotency and persisted response

ReportId is scoped to the leased attempt, not globally reserved. Failed validation,
ownership checks, SaveChanges and rollback reserve nothing. PostgreSQL persists the
accepted ReportId and original response snapshot; no process-local cache is used.
The exact outer request is not stored. Snapshot fields are exactly jobId, attemptId,
leaseId, reportId, lowercase outcome, finishedAtUtc and result for success or error
for failure. All response timestamps use UTC Z. Payload, Worker configuration,
definition metadata, submission identity, token/hash and persistence fields are excluded.

The snapshot stores validated result value text as a JSON string, preserving its
original spelling/property order; a separate Domain result column uses jsonb.
First success and replay return this snapshot, never later mutable Job state.
Replay retains finish/release times and works after API restart. Session replacement
fences even an otherwise equivalent replay.

Equivalence compares outcome and decoded error strings ordinally. Result structural
equality ignores object property order, whitespace and escaping. Decoded names and
strings compare ordinally without Unicode normalization. Array order matters.
Numbers compare exact mathematical decimal values without floating-point rounding
(`1`, `1.0`, `1e0` are equivalent; signed zeros are equivalent). Different JSON
kinds differ. Validated duplicate-free results use JsonElement.DeepEquals.

### Late completion and future finalizer

Expiration alone does not finalize execution in 2C. Late completion is accepted
while Job/attempt are Running, Lease unreleased and current session/token valid.
It does not renew ownership or restore past capacity: expired leases already
stopped consuming slots under ADR-0012. This tolerates network delay; it is not
lost-execution recovery. No retry is created.

Slice 2D's finalizer must lock the same rows in the same order below. The first
committed terminal transition wins. A report losing to finalization receives
attempt_already_finalized; finalization must preserve a committed completion.
No finalizer, background monitor or terminal loss state is implemented in 2C.

### Transactions and locks

Application owns separate focused IRenewLeasePersistence and
IReportExecutionCompletionPersistence READ COMMITTED transactions. Persistence
owns SQL, row locking, shadow protocol columns and rollback tracking cleanup.
There is no generic repository, UnitOfWork, mediator or CQRS infrastructure.

1. Optional read-only Lease locator finds JobId/AttemptId; it may execute after
   Worker validation. This locator grants no ownership.
2. Worker FOR UPDATE; check existence and current session.
3. Job FOR UPDATE.
4. JobAttempt FOR UPDATE.
5. Lease FOR UPDATE.
6. Revalidate all located IDs, relationships and ownership after locks; sample
   time and perform lifecycle/replay validation.
7. Mutate and save; commit before any success, including replay.

Never lock Worker after Job/attempt/Lease. Registration, heartbeat, claim, renewal
and completion for one Worker serialize on Worker. Different Workers share no
global lock. READ COMMITTED statements after waiting see the preceding commit.
Concurrent renewals cannot lose extensions; renewal/completion and competing reports
produce one consistent terminal state. Replacement first fences old-session work;
operation first retains that session's committed result. Claim before completion
sees active capacity occupied; claim waiting behind completion uses a slot only
after commit. Disposal rolls back unfinished work and detaches transaction entities
and shadow snapshots even after successful SaveChanges followed by commit failure.
Reusing a scope must read committed state. Cancellation is never caught as success.

See PostgreSQL [row locks](https://www.postgresql.org/docs/17/explicit-locking.html#LOCKING-ROWS),
[READ COMMITTED](https://www.postgresql.org/docs/17/transaction-iso.html#XACT-READ-COMMITTED),
[jsonb limits](https://www.postgresql.org/docs/17/datatype-json.html) and
[JsonElement.DeepEquals](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsonelement.deepequals?view=net-10.0).

### Schema and enforcement boundary

Add nullable bytea token_hash with an exact 32-byte length check; null accommodates
legacy rows without backfill. Add nullable result jsonb, completion_report_id UUID
and persistence-only completion_snapshot jsonb on job_attempts, reusing error columns.
Check non-null ReportId RFC UUID v7 version/variant, paired report/snapshot, object
result, and for reported attempts terminal status, finish >= start, and the matching
success/result/no-error or failure/error/no-result shape. Existing Job/attempt/Lease
and Worker/session FKs and unique attempt/Lease and (JobId, number) remain unchanged.

Do not constrain every legacy terminal row to have a worker report: future loss
finalization also needs reportless outcomes. Direct SQL can still insert tokenless
leases and reportless legacy shapes. Domain/Application enforce new-write rules,
Unicode/received-size/depth limits, snapshot immutability and cross-row status/time
consistency. DB constraints enforce reported attempt shapes and relationships,
not cross-row lifecycle consistency or original JSON spelling. No trigger or
permanent one-attempt-per-Job constraint is justified. Preserve all legacy rows.

## Consequences and next increment

Slice 2C provides renewable ownership and idempotent success/failure with atomic
slot release. Client GetJob remains ADR-0009 with no result/outcome expansion.
Slice 2 stays incomplete until submit-to-result execution and guaranteed loss
finalization work. Next is Slice 2D loss finalization without retry, then Slice 2E
minimal Worker and bounded workload, then Slice 2F client-visible terminal outcome
and small result. No execution, cancellation, standalone release endpoint,
authentication, polling policy, distributed cache or retention is added here.
