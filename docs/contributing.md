# Contribution Conventions

## Branch names

Branch names use a category followed by a short kebab-case description:

```text
<category>/<kebab-case-description>
```

Use these categories:

| Category | Purpose |
|---|---|
| `feature` | New user-visible or domain capability |
| `fix` | Defect correction |
| `docs` | Documentation-only change |
| `test` | Test-only change |
| `refactor` | Code restructuring without a behavior change |
| `perf` | Performance improvement |
| `build` | Build system or dependency change |
| `ci` | Continuous-integration change |
| `chore` | Repository maintenance not covered above |

Descriptions use lower-case English words separated by hyphens. Prefer two to
six words that identify the outcome rather than an implementation detail. Do
not use personal, tool, or agent-name prefixes. Add an issue identifier only
when the project starts requiring one; do not invent placeholder identifiers.

Examples:

```text
feature/submit-job
fix/solution-doc-items
docs/worker-api-semantics
```

The branch category `feature` corresponds to the Conventional Commit type
`feat`; the longer word is retained for branch readability.

## Commit titles

Commit titles follow Conventional Commits:

```text
<type>[(<scope>)][!]: <summary>
```

Use these types:

| Type | Purpose |
|---|---|
| `feat` | New user-visible or domain capability |
| `fix` | Defect correction |
| `docs` | Documentation-only change |
| `test` | Test-only change |
| `refactor` | Code restructuring without a behavior change |
| `perf` | Performance improvement |
| `build` | Build system or dependency change |
| `ci` | Continuous-integration change |
| `chore` | Repository maintenance not covered above |

The scope is optional and should name a stable area such as `jobs`, `workers`,
`api`, or `persistence`, not a file name. Use `!` and a `BREAKING CHANGE:`
footer when a commit intentionally breaks a public contract.

Write the summary in imperative, lower-case English, without a trailing period.
Keep the complete title at 72 characters or fewer. A commit should represent one
coherent change; explain motivation, trade-offs, and relevant issue or ADR links
in the body when the title is insufficient.

Examples:

```text
docs: define submit job semantics
feat(jobs): add submit job use case
fix(persistence): serialize submission with definition updates
```

## Pull request titles

Pull request titles use the same format and type vocabulary as commit titles:

```text
<type>[(<scope>)][!]: <summary>
```

The title describes the complete change delivered by the pull request, not its
individual implementation steps. Use a scope only when it makes the affected
area materially clearer. Keep the title at 72 characters or fewer, use
imperative lower-case English, and omit a trailing period.

When a pull request contains several commit types, choose the type that reflects
its primary externally meaningful outcome. A documentation-only pull request
uses `docs`; a capability implemented together with its tests and documentation
uses `feat`.

Examples:

```text
docs: define submit job semantics
feat(jobs): implement submit job
```

Do not add a pull request number manually to source commit titles or the pull
request title. When squash-merging, retain the `(#N)` suffix that GitHub adds to
the final commit title because it provides a direct link from repository history
to the pull request. The generated suffix does not count against the 72-character
limit for the authored PR title. Historical commits without the suffix do not
need to be rewritten.

Example final squash commit:

```text
feat(jobs): implement submit job (#12)
```

## Repository files in the solution

Every Markdown file under `docs/` must also appear in the matching documentation
folder in `Synestra.slnx`, so Solution Explorer mirrors the repository's
documentation structure. Every file under `scripts/` must likewise appear in
the matching solution folder because these scripts are part of the development
and CI workflow. Adding, moving, or removing either kind of file requires
updating the solution in the same change.

Run the consistency check from the repository root:

```powershell
./scripts/verify-solution-items.ps1
```

CI runs the same check and rejects documentation or script files missing from
the solution, stale solution references, and files placed in a solution folder
that does not match their filesystem directory.
