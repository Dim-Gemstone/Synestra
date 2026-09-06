# Planning and execution workflow

This workflow keeps a roadmap increment small enough to implement and review
without treating chat history as durable project context.

## Context across chats

Codex chats do not automatically share their prior messages. A new chat can read
the repository, including accepted ADRs and committed documentation, but cannot
retrieve a brief, decomposition, or handoff from another chat unless that content
is pasted into the new prompt or saved in the repository.

For a new increment, create one planning chat. Ask it to inspect the repository
without edits and produce an execution-ready shared brief plus a decomposition.
Keep the roadmap at the increment level; do not add the detailed plan there.

The planning chat may then implement a chosen first unit directly. There is no
need to create an intermediate chat merely to "remember" the brief.

For a later chat, provide:

- the selected unit instruction;
- the previous unit's handoff, if there is one;
- the branch name and whether its changes are committed or intentionally dirty;
- any brief decisions not yet recorded in an accepted ADR.

Once a contract is accepted, record it in an ADR. That makes later implementation
chats independent of archived conversation history.

## Execution units

An execution unit is a coherent vertical behavior. It includes the required
Domain, Application, Persistence, API or host changes for that behavior, its
focused tests, and only the documentation needed to describe completed work.
It must leave the repository buildable and testable.

Do not split a unit into a DTO, controller, repository, and tests. Split by a
useful boundary such as atomic finalization of one execution, a bounded discovery
pass, or host scheduling of an already-tested pass.

Each decomposition states dependencies, exclusions, done conditions, a size
(`Medium` or `Large`), and the recommended model/reasoning effort. The person
selecting a batch decides which consecutive units to implement; Codex must stop
after that batch.

## Prompt templates

### Planning or decomposition

```text
Prepare work instructions for Synestra.

ROADMAP INCREMENT: <name>
MODE: <brief_only | brief_and_units | decompose_existing_brief>

This is analysis only. Do not edit files, create a branch, commit, push, or open
a pull request.

Read AGENTS.md, git status, relevant documentation and accepted ADRs, then inspect
the neighboring code, migrations, and tests. Treat accepted ADRs as authoritative.

For brief_only, produce an execution-ready shared brief.
For brief_and_units, produce that brief and a decomposition.
For decompose_existing_brief, preserve the supplied semantics, identify conflicts
with the repository, and produce a decomposition.

The brief must define: goal and done condition; prerequisites; proposed ADR
decisions; Domain/Application/Persistence/API or host boundaries; schema and legacy
compatibility; concurrency and failure behavior; tests; docs; exclusions;
verification; roadmap checkpoint; and the next increment.

For every execution unit, state dependencies, scope, exclusions, done condition,
size (Medium/Large), and recommended model/reasoning. Keep units vertical and
independently testable. Do not put detailed execution units into docs/roadmap.md.

Do not present proposed decisions as accepted ADRs. Do not invent exact token or
usage estimates.

EXISTING BRIEF OR ADDITIONAL CONSTRAINTS:
<paste if applicable>
```

### Implementing a chosen batch

```text
Continue Synestra.

BATCH: <for example, 2D.1 only or 2D.2 + 2D.3>
MODEL: GPT-6 Astra
REASONING: <high | xhigh>
BRANCH: <branch name>

Implement only the selected units from the brief below. Read AGENTS.md, verify
the dependency state and preserve existing worktree changes. Do not create a
commit, push, or pull request without separate confirmation. Do not proceed to
an unselected unit.

Run the verification required by AGENTS.md and the selected unit. At the end,
report tests, git status, a roadmap checkpoint, a Conventional Commit title, and
the handoff format below.

HANDOFF FROM THE PREVIOUS BATCH:
<paste, or write "none">

SELECTED UNIT INSTRUCTIONS:
<paste the shared brief decisions needed by this unit and the selected unit block>
```

## Handoff format

```text
Branch: <name>
Base: <commit SHA>
Completed units: <IDs>
Remaining units: <IDs>
Working tree: <clean | changed; list intended files>
Verification: <commands and results>
Contract state: <accepted ADR IDs or decisions still only in the brief>
Open questions: <none or list>
Next command: <copy-paste batch prompt or its key instruction>
```
