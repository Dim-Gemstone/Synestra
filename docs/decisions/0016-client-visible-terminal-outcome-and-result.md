# ADR-0016: Client-Visible Terminal Outcome and Small Result

**Status:** Accepted

## Context and delivery boundary

ADR-0013/0014 persist reported completion and abandoned execution; ADR-0015
executes a bounded workload. ADR-0009's seven-field GET cannot expose that output.
This decision defines Slice 2F. Unit 2F.1 implements the terminal read contract;
unit 2F.2 separately qualifies the complete Client/Worker/process path before
marking Slice 2 complete. Existing lifecycle and Worker protocols remain intact.

## Decision

### Client representation

Extend `GET /api/client/jobs/{id}` with `completedAtUtc` and `completion`.
Preserve the original seven fields and their meanings. Both new fields are
always present, including when null. For example, the additional fields are:

```json
{
  "completedAtUtc": "2026-09-08T12:00:40Z",
  "completion": {
    "attemptId": "019f267c-0800-7000-8000-000000000001",
    "attemptNumber": 1,
    "outcome": "succeeded",
    "result": { "count": 4, "sum": 4 },
    "error": null
  }
}
```

Completion has exactly those five fields, with explicit null result/error.
Error is null or exactly `code` and `message`. Result is an embedded JSON object,
never an encoded JSON string. Timestamps are persisted UTC values with `Z`;
completedAtUtc is Job.CompletedAtUtc, not lease expiration or current read time.

| Persisted execution | Job status | Completion outcome | Result | Error |
|---|---|---|---|---|
| Pending/Running under current writes | pending/running | null completion | — | — |
| Worker success | succeeded | succeeded | Object | null |
| Worker failure | failed | failed | null | Reported code/message |
| Finalized loss | failed | abandoned | null | Stored execution_lease_expired error |

Current Pending/Running writes also have null completedAtUtc. Succeeded describes
protocol completion; business interpretation belongs to the workload result.
An existing Job returns 200 application/json, including failed execution.
Missing Job retains ADR-0009's 404 RFC 9457 `job_not_found` contract.

POST retains ADR-0007/0010's original seven-field response, Location, request
identity and exact durable replay. A replay after completion still returns its
original submission snapshot; GET returns current persisted state. No change to
the serialized submission model or stored snapshots is needed.

### Attempt association and legacy compatibility

First select the Job's attempt with greatest Number, using the existing unique
(JobId, Number) index. Never assume number 1, order by UUID, or filter terminal
attempts before selecting the latest. Completion is available only when:

- Job.CompletedAtUtc is non-null and equals that attempt's FinishedAtUtc;
- Job Succeeded matches attempt Succeeded, or Job Failed matches attempt Failed
  or Abandoned.

If the latest attempt is absent or does not match, return null completion without
falling back to an older attempt. Preserve Job metadata, including its nullable
completion timestamp. Legacy Cancelled likewise has null completion; no cancellation
semantics are introduced. GET never repairs or finalizes rows.

An associated legacy terminal attempt may lack result or report. Preserve its
known outcome and return null for missing output, never a fabricated empty object
or synthetic report. For Failed/Abandoned return an error object only when both
stored code and message exist; otherwise error is null. Succeeded always has null
error; Failed/Abandoned always have null result. New valid Worker completions
continue to have their required result/error.

### Result representation and limits

For reported success, Persistence extracts only the original result text from
`completion_snapshot`'s `Result` string, as stored by ADR-0013. The snapshot is a
representation source, not a replacement for Job/attempt lifecycle state. Do not
load the whole Worker snapshot or share its response DTO with the Client API.
JSON object semantics and exact numeric values are preserved without conversion
through double or decimal; textual formatting is not a Client replay guarantee.

This avoids jsonb's expansion of accepted compact values such as `1e131071`.
ADR-0013 already bounds the original result to 64 KiB received UTF-8 and depth 32.
The response envelope adds two levels. JSON escaping can enlarge serialized output;
64 KiB is not a cap on the entire GET response.

For legacy success without a snapshot, read the existing result object only if
its PostgreSQL text representation is at most 65,536 UTF-8 bytes. Check that bound
in SQL before transferring the value to the API. Missing or larger legacy output
returns null, without truncation or backfill. Null legacy result therefore means
unavailable through this read contract, not an empty successful output. Legacy
JSON nesting is not retroactively validated as a new Worker report; serialization
must accommodate the bounded legacy value. Worker input limits remain unchanged.

### Data exposure and architecture

Result and error are Client-visible Job data inside the current trusted/private
deployment boundary. Workload authors must keep secrets out of published output.
There is no authentication, workload interpretation or general redaction framework
in this slice. Payload, definition internals, Worker/session/Lease identifiers,
tokens/hashes, ReportId, full snapshots and complete history remain excluded.

Application keeps focused GetJob and a separate read model. The existing JobDetails
submission model and its persistence serialization remain unchanged. Persistence
projects only required fields in one read-only SQL statement, without tracking,
row locks, caches or a separate transaction. PostgreSQL's statement snapshot
provides coherent Job/attempt visibility before or after a concurrent terminal
commit. Do not split the read across independently committed statements.
API maps the read model to its transport representation. Domain, Worker and
finalizer transitions remain unchanged; no schema migration or new index is needed.

### Failure and concurrency

Expiration alone does not produce a Client abandoned outcome. Only committed loss
finalization does so. Reading cannot renew, claim, finalize or retry work. Preserve
ADR-0014's first-committed terminal transition rule and ADR-0013's replay snapshot
and timestamps. Cancellation propagates through asynchronous reads. Legacy
fallbacks concern absent/inconsistent persisted data, not database failures;
unexpected infrastructure failures retain the existing internal_error handling.

## Verification and next increment

Unit 2F.1 covers the read states, legacy compatibility, latest-attempt selection,
single-statement visibility around terminal commit, bounded JSON projection,
numeric/depth/Unicode boundaries, exact Client field sets, API restart and
unchanged submission snapshots/replay. Existing seven-field GET assertions evolve;
POST and Worker protocol assertions retain their contracts.

Unit 2F.2 still needs Client submit-to-result qualification with real Worker
execution, response-loss/replay and finalization races, plus separate-process
Client observations. Slice 2 stays incomplete until that unit passes. Slice 3
then addresses observing and cancelling a long-running scenario. Progress,
partial results, artifacts, history/list endpoints, UI, retry, pause, browser
runtime, definition management, auth, retention and deployment policy are excluded.
