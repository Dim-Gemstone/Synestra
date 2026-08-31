# ADR-0006: Job Definition Identity

**Status:** Accepted

## Context

Clients submit jobs for a registered workload type. `JobDefinition.Type` is the
natural machine-readable key for that interaction, while persisted jobs must
retain a durable relationship to the definition that accepted them.

Using only the definition UUID in client contracts would make configuration,
diagnostics, and worker routing unnecessarily opaque. Using only the type string
in persistence would leave the relationship unenforced and would make historical
jobs depend on the current record found under that string.

## Decision

Clients identify a job definition by its unique textual `Type`.

`JobDefinition.Id` is the stable relational identity of a definition.
`JobDefinition.Type` is its unique, immutable machine-readable key.

Each `Job` stores both:

- `JobDefinitionId`, as a foreign key to the definition that accepted the job;
- `Type`, as an immutable snapshot of the machine-readable type at submission.

PostgreSQL enforces the pair with a composite foreign key from
`Job(JobDefinitionId, Type)` to `JobDefinition(Id, Type)`. This prevents a job
from combining the identity of one definition with the type of another.

The relationship uses restrictive delete behavior. A job definition referenced
by a job cannot be physically deleted. Disabling or later archiving definitions
is preferred over deleting execution history.

Changing a workload contract incompatibly must not silently reuse an existing
type. Contract versioning will be introduced when a concrete workload requires
it; this decision does not prescribe its representation in advance.

## Consequences

### Positive

- Client contracts retain a readable and stable workload key.
- PostgreSQL enforces that every job refers to an existing definition.
- PostgreSQL enforces that the stored ID and type belong to the same definition.
- Historical jobs retain the type under which they were submitted.
- Internal relationships do not depend solely on a mutable business string.

### Trade-offs

- `Job.Type` duplicates the definition type intentionally as historical data.
- The composite principal key adds a small redundant index on definitions; the
  corresponding composite job index also serves queries filtered by definition
  ID through its leftmost prefix.
- Creating a job requires resolving the supplied type to a definition.
- Physical deletion of referenced definitions is not available.

## Not Decided Here

This ADR does not define:

- behavior when submitting against a disabled definition;
- definition management or authorization;
- payload schema or contract version representation;
- scheduling or retry inputs;
- submission idempotency;
- API request, response, or error contracts.
