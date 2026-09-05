# ADR-0009: Observe Job Contract

**Status:** Accepted

## Context

Slice 1 accepts and persists a Job, but a client cannot retrieve it after the
submission response is lost or after the API process restarts. ADR-0007 defines
the submission representation and deliberately leaves Job retrieval undecided.

The first read surface must expose enough persisted state to identify and track
an accepted Job without prematurely defining execution progress, results,
attempt history, or access to its opaque workload input.

## Decision

### Read endpoint and representation

The Client API exposes:

```text
GET /api/client/jobs/{id}
```

For an existing Job, the endpoint returns `200 OK` with `application/json` and
this minimal representation:

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

These fields match the stable successful submission representation from
ADR-0007. Timestamps use UTC with the `Z` designator and status uses its
lower-case API representation.

The Application layer owns a focused GetJob query. Persistence reads the Job
from PostgreSQL without tracking and projects only the fields in the
Application read model. The API maps that read model to its transport contract.
The query does not use an in-memory cache and needs no explicit transaction for
its single statement.

### Data exposure

The response does not expose `Payload`. Payload is opaque workload input and
may contain sensitive or unnecessarily large data; Slice 1A has no concrete
client requirement that justifies returning it. A future payload-read contract
requires a separate decision covering authorization, redaction, and resource
limits.

The internal `JobDefinitionId`, attempts, progress, results, artifacts, and
control state are also excluded. Their omission does not define future
execution-observation contracts.

### Missing Job

When no Job has the requested ID, the endpoint returns RFC 9457 Problem Details
with `404 Not Found`, type `urn:synestra:problem:job-not-found`, and stable code
`job_not_found`. As in ADR-0007, clients use `code` rather than diagnostic text
as the machine-readable identifier.

### Submission Location

Once this endpoint exists, a successful `POST /api/client/jobs` response
includes a `Location` header containing `/api/client/jobs/{id}` for the created
Job. Its existing `201 Created` body and all other ADR-0007 contracts remain
unchanged.

## Consequences

- A client can recover the persisted state of an accepted Job by ID, including
  after API restart.
- The read path remains independent of process memory and does not load payload
  data that the response cannot expose.
- Submission gains a stable resource location without changing its body.
- The database schema does not change.

## Not Decided Here

This ADR does not define:

- listing, filtering, sorting, or pagination;
- execution progress, results, artifacts, or attempt-history representations;
- payload retrieval or workload-specific input schemas;
- caching, conditional requests, or retention;
- authentication or authorization;
- any Job or JobAttempt lifecycle transition.
