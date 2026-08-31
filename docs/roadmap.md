# Roadmap

## Slice 1 — Submit a job

A client can submit a Job for an enabled JobDefinition.

Done when:

- application use case exists;
- invalid definition is rejected;
- disabled definition behavior is explicitly defined;
- Job is persisted;
- API endpoint exists;
- domain tests exist;
- PostgreSQL integration test exists.

Not included:

- worker dispatch;
- retries;
- cancellation;
- submission idempotency.

Decisions resolved before implementation:

- behavior for submitting work against a disabled definition — resolved by ADR-0007;
- which scheduling and retry inputs the client may provide — resolved by ADR-0007;
- ownership of job ID and creation timestamp generation — resolved by ADR-0007;
- minimum JSON payload validation and size rules — resolved by ADR-0007;
- submission response, error, and transaction contracts — resolved by ADR-0007.

Implementation requirements:

- add a focused `SubmitJob` Application use case and make it the transaction
  boundary;
- resolve and `FOR SHARE` lock the definition by type at `READ COMMITTED`;
- reject missing and disabled definitions with the ADR-0007 error codes;
- validate the strict request shape, UTC availability, object payload, 256 KiB
  payload limit, depth 32, and duplicate property names;
- obtain time from an injectable server clock and preserve Domain-owned UUID v7
  Job ID generation;
- create Jobs with priority 0, maximum attempts 1, and availability defaulted to
  creation time;
- persist the definition ID and type snapshot and return success only after
  commit;
- expose the ADR-0007 `201 Created` response and RFC 9457 error contract through
  a thin Client API endpoint;
- cover domain defaults/invariants, PostgreSQL persistence and disable/submit
  concurrency, and API request/response/error contracts with tests.
