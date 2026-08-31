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

Decisions required before implementation:

- behavior for submitting work against a disabled definition;
- which scheduling and retry inputs the client may provide;
- ownership of job ID and creation timestamp generation;
- minimum JSON payload validation and size rules;
- submission response contract;
