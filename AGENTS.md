# Synestra

Synestra is a workload-agnostic distributed execution control plane for
coordinating background work across independently running workers.

The initial product form is a control-plane service. Reusable libraries or a
framework may emerge later, but the core must not be designed around that
possibility before concrete reuse requirements exist.

The core domain concepts are:

- JobDefinition — registered type of executable work.
- Job — one logical unit of work.
- JobAttempt — one execution attempt for a Job.
- Lease — temporary exclusive right for a Worker to execute an attempt.
- Worker — registered worker agent with identity, heartbeat and finite capacity.

See:
- docs/project-brief.md
- docs/domain-glossary.md
- docs/job-lifecycle.md
- docs/project-status.md
- docs/roadmap.md
- docs/contributing.md
- docs/testing.md
- docs/decisions/README.md

Before implementation, review `docs/roadmap.md` and the relevant accepted
ADRs. Accepted ADRs are authoritative if summary documents such as
`docs/project-status.md` or `docs/roadmap.md` temporarily lag behind them.

## Repository and solution structure

- Production projects belong under `src/`, test projects under `tests/`, and
  Aspire orchestration projects under `orchestration/`.
- Every Markdown file under `docs/` and every file under `scripts/` must appear
  in the corresponding solution folder in `Synestra.slnx`.
- When adding, moving, renaming, or deleting a project, document, or script,
  update `Synestra.slnx` in the same change. Solution folders must mirror the
  corresponding filesystem directories.
- Before completing any change, run `./scripts/verify-solution-items.ps1`.
- Before completing an implementation change, run
  `dotnet test --solution Synestra.slnx`. See `docs/testing.md` for the full
  testing guide and `docs/contributing.md` for branch, commit, and pull request
  conventions.
- Before committing, run `git diff --check`, review the complete diff, and
  inspect `git status --short --branch`.

## Git workflow

Follow the branch, commit, and pull request conventions in
`docs/contributing.md`.

- When a task requires a dedicated branch and the user has not specified its
  name, infer a compliant branch name from the requested outcome and state the
  proposed name before creating it.
- After completing a change, always propose one Conventional Commit title based
  on the final diff, even if the user did not explicitly request one.
- Do not create a commit or pull request without explicit user confirmation.

## Architecture

Projects should follow these dependency directions:

```text
API -> Application -> Domain
API -> Persistence -> Domain
```

The exact Application-to-Persistence boundary is intentionally undecided and
must emerge from concrete use cases. Application must not depend on the
concrete EF Core Persistence project.

Domain must not depend on Application, API, EF Core or infrastructure concerns.

Persistence implements storage concerns and EF Core mappings.

Application owns use-case orchestration and transaction boundaries.

API should remain thin and must not contain domain workflow logic.

Client-facing and worker-facing operations are separate logical API surfaces,
even when hosted by the same application. Workers communicate through the
Worker API and must not access the control-plane database directly. Follow
ADR-0005 for this boundary.

Do not introduce repositories, mediator abstractions, CQRS infrastructure,
domain events or other architectural patterns unless they solve a concrete
problem in the current implementation.

## Domain rules

- Domain entities have no public setters.
- State transitions happen through meaningful domain methods.
- Invalid state transitions must be rejected by the domain.
- Use UTC for persisted timestamps.
- IDs use UUID v7 unless there is a concrete reason otherwise.
- EF Core concerns must not leak into the public domain model.
- Job and JobAttempt lifecycle semantics must follow docs/job-lifecycle.md.
- Never invent lifecycle behavior that is marked as undecided in documentation.

## Persistence

- PostgreSQL is the source of truth for job coordination.
- Concurrency-sensitive operations must be designed explicitly.
- Job claiming, lease expiration and retry behavior must be safe under
  concurrent workers.
- Do not rely on in-memory locking for distributed coordination.
- Prefer database-enforced constraints for invariants that must hold globally.

## Application layer

Application code should express use cases, not generic infrastructure.

Prefer focused services/use cases such as:

- SubmitJob
- RegisterWorker
- ClaimWork
- RenewLease
- CompleteAttempt
- FailAttempt

Avoid creating generic abstractions before multiple concrete use cases need them.

## Testing

- Domain state transitions: unit tests.
- EF mappings and queries: PostgreSQL integration tests.
- Claiming, leasing and concurrency: real PostgreSQL concurrency tests.
- API contracts: API/integration tests.
- Do not mock EF Core DbContext.

## Coding

- .NET 10 / modern C#.
- Nullable reference types enabled.
- File-scoped namespaces.
- Prefer `var` when the type is obvious from the right-hand side.
- Pass CancellationToken through asynchronous application/infrastructure code.
- Avoid unnecessary comments; document why, not what.
- Do not suppress compiler/analyzer warnings without justification.

## Agent behaviour

Before making architectural changes:

1. Inspect the relevant domain documentation.
2. Inspect existing conventions in neighboring code.
3. Prefer the smallest change consistent with current architecture.
4. Do not silently resolve an open architectural/domain question.
5. If implementation requires an undecided semantic rule, stop and report the
   decision that is required.

When asked only to analyze code, do not modify files.

Keep generated migrations limited to schema changes implied by the requested
model change. Do not manually clean up generated migration code unless needed.

After completing a roadmap slice or another substantial implementation change,
include a brief roadmap checkpoint in the final response that states:

- which milestone or slice was completed;
- what now works end-to-end;
- the next roadmap slice;
- what still separates the project from a minimally useful product.

Base the checkpoint on `docs/roadmap.md`, accepted ADRs, and verified
implementation. Do not present planned or partially implemented work as
complete. Omit the checkpoint for minor fixes and other non-substantial changes.
