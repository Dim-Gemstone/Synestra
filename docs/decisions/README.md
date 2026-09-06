# Architecture Decision Records

This directory contains decisions that are important enough to preserve together with their context and rationale.

## Statuses

- **Proposed** — under consideration.
- **Accepted** — currently authoritative.
- **Rejected** — considered and explicitly not chosen.
- **Superseded** — previously accepted but replaced by another decision.

Deferred ideas that have not yet required an architectural decision should normally remain in `../project-status.md` instead of receiving an ADR.

## Decision Index

| ADR | Title | Status |
|---|---|---|
| 0001 | Project and dependency boundaries | Accepted |
| 0002 | PostgreSQL as primary persistence | Accepted |
| 0003 | Dedicated database migration worker | Accepted |
| 0004 | Browser automation as the initial workload | Accepted |
| 0005 | Worker communication through the control-plane API | Accepted |
| 0006 | Job definition identity | Accepted |
| 0007 | Submit job semantics | Accepted |
| 0008 | Execution unit and orchestration boundary | Accepted |
| 0009 | Observe Job contract | Accepted |
| 0010 | Idempotent Job submission | Accepted |
| 0011 | [Worker registration and liveness](0011-worker-registration-and-liveness.md) | Accepted |
| 0012 | [Atomic Job claim and execution ownership](0012-atomic-job-claim.md) | Accepted |
| 0013 | [Lease renewal and execution reporting](0013-lease-renewal-and-execution-reporting.md) | Accepted |
| 0014 | [Lost-execution finalization without retry](0014-lost-execution-finalization.md) | Accepted |

See `../project-status.md` for unresolved questions, proposals, rejected directions, and deferred work.
