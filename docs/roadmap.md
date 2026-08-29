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
- cancellation.
