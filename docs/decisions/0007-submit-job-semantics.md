# ADR-0007: Submit Job Semantics

**Status:** Accepted

## Context

The first vertical slice allows a client to submit a `Job` for a registered
`JobDefinition`. The existing model already stores the definition identity and
type snapshot, arbitrary JSON payload, priority, maximum attempt count,
creation time, and availability time. Those fields alone do not define who may
supply them, how invalid submissions fail, or how submission behaves when a
definition is disabled concurrently.

Slice 1 requires a deliberately small contract. Retry execution, submission
idempotency, recurring scheduling, authentication, and definition management
remain outside this decision.

## Decision

### Definition eligibility

Clients identify the definition by its unique textual `Type`, as established
by ADR-0006.

A disabled definition rejects new submissions. Disabling a definition does not
cancel, suspend, or otherwise change jobs that were accepted earlier, including
pending jobs.

The Client API distinguishes a missing definition from a disabled definition.

### Submission request

The minimum request contains:

```json
{
  "type": "browser.capture-page",
  "payload": {},
  "availableAtUtc": "2026-09-01T12:00:00Z"
}
```

`type` and `payload` are required. `type` must be a non-empty string no longer
than 100 characters, consistent with the domain and persisted definition key.
`availableAtUtc` is optional and is the only client-controlled scheduling input
in Slice 1. When supplied, it must be an explicit UTC timestamp and must not
precede the server-generated `CreatedAtUtc`. Clients omit it for immediate
availability.

Unknown request properties are rejected. Slice 1 does not accept deadlines,
timeouts, recurring schedules, affinity, capability requirements, or other
scheduling inputs.

Clients cannot supply `priority` or `maxAttempts`. Accepted jobs use
`Priority = 0` and `MaxAttempts = 1`. One is the total permitted attempt count,
so Slice 1 does not enable retries. These fixed values do not decide how a
future priority or retry policy will be configured.

### Server-owned values

The server owns job identity and timestamps:

- the Domain creates the Job ID as UUID v7;
- the Application use case obtains `CreatedAtUtc` from an injectable server UTC
  clock;
- when `availableAtUtc` is omitted, the Application sets `AvailableAtUtc` equal
  to `CreatedAtUtc`;
- when it is supplied, the Application validates and passes the normalized UTC
  value to the Domain.

Clients cannot supply the Job ID or creation timestamp.

### Payload contract

`payload` must be a non-null JSON object. Arrays, scalar values, and `null` are
rejected. Slice 1 performs no workload-specific schema validation.

The payload JSON value is limited to 256 KiB in its received UTF-8
representation and to a maximum nesting depth of 32. Duplicate property names
are rejected. The HTTP request-body limit may include a small transport-level
allowance for the surrounding request fields, but it must not weaken the
payload limit.

The accepted payload is persisted as PostgreSQL `jsonb`.

### Successful response

A successful submission returns `201 Created` with `application/json` and this
representation:

```json
{
  "id": "019...",
  "type": "browser.capture-page",
  "status": "pending",
  "priority": 0,
  "maxAttempts": 1,
  "createdAtUtc": "2026-09-01T11:55:00Z",
  "availableAtUtc": "2026-09-01T12:00:00Z"
}
```

The response does not echo the payload. A `Location` header is included only
when a stable endpoint for retrieving that Job exists or is defined together
with the implementation; the submit contract does not invent such an endpoint.
All response timestamps use UTC with the `Z` designator.

### Error contract

API errors use RFC 9457 Problem Details with
`Content-Type: application/problem+json`. Every problem contains `type`,
`title`, `status`, and the extension member `code`. `code` is the stable
machine-readable identifier; clients must not parse `title`, `detail`, or
`traceId` as identifiers. `type` uses
`urn:synestra:problem:<kebab-case-code>`. `detail`, `instance`, and `traceId`
are optional diagnostic members.

The Slice 1 mappings are:

| Condition | HTTP status | Code |
|---|---:|---|
| Malformed JSON, unknown fields, or invalid type, timestamp, payload shape, duplicate property, or depth | 400 | `invalid_request` |
| Payload exceeds 256 KiB | 413 | `payload_too_large` |
| Definition type does not exist | 404 | `job_definition_not_found` |
| Definition is disabled | 409 | `job_definition_disabled` |
| Unexpected server failure | 500 | `internal_error` |

Validation problems may include an `errors` extension with the stable shape
`object<string, string[]>`: each key is a JSON field path and each value is one
or more human-readable messages. Message text is not a machine-readable
contract. A trace identifier may be included for diagnostics but is not part
of domain semantics.

### Transaction and concurrency semantics

Submission is one Application-owned PostgreSQL transaction at `READ COMMITTED`:

1. Resolve the definition by `Type` and lock its row with `FOR SHARE`.
2. In the same transaction, reject the definition if it is disabled.
3. Create and insert the Job with the resolved `JobDefinitionId` and `Type`
   snapshot.
4. Commit before reporting success.

Future definition enable/disable operations must update the definition row in
a transaction. PostgreSQL row-update locking conflicts with the submission's
`FOR SHARE`, so submission is ordered with respect to an enable/disable change:
it either commits before disabling or observes the disabled state and fails.

The composite foreign key from ADR-0006 remains the database-enforced
relationship safeguard. No Job is persisted when validation or commit fails.

## Consequences

### Positive

- Slice 1 has a small deterministic request and response contract.
- Disabled definitions cannot race with new accepted submissions.
- Existing jobs are insulated from later definition enablement changes.
- Retry and priority policy are not accidentally exposed before their semantics
  exist.
- Payload resource bounds are explicit before untrusted JSON is persisted.
- API clients receive stable machine-readable failure categories.

### Trade-offs

- Object-only payloads exclude valid JSON scalar and array roots.
- Strict unknown-field and duplicate-property handling requires explicit JSON
  validation.
- `FOR SHARE` briefly serializes submission with definition updates.
- Clients cannot request priority or retry behavior in the initial slice.

## Not Decided Here

This ADR does not define:

- submission idempotency;
- authentication or authorization;
- definition creation or management APIs;
- retry, backoff, or failure-classification semantics;
- future ownership of configurable priority or retry defaults;
- Job retrieval endpoints;
- workload-specific payload schemas or schema version representation;
- recurring scheduling, deadlines, timeouts, or cancellation.
