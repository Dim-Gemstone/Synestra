# Domain Glossary

This glossary defines the shared language of Synestra. It intentionally does
not assign lifecycle ownership or aggregate boundaries that have not yet been
decided.

| Term | Meaning | Not | Confirmed semantics | Still undecided |
|---|---|---|---|---|
| `JobDefinition` | Registered type of executable work | A submitted execution instance | Has a unique type and can be enabled or disabled | Who manages definitions and how disabling affects existing jobs |
| `Job` | One logical unit of submitted work | One physical execution | Can have multiple attempts and preserves their history | Exact status derivation, cancellation and retry semantics |
| `JobAttempt` | One execution try for a Job | The retry policy itself | Belongs to one Job and records one execution outcome | Exact creation point and state after lease expiration |
| `Lease` | Time-bounded exclusive right for a Worker to execute an attempt | A permanent lock or execution result | Has acquisition and expiration times and cannot permanently assign work | Renewal, expiration recovery and late-result behavior |
| `Worker` | Registered execution process with identity, liveness and finite capacity | An OS thread, HTTP request or browser instance | May disappear and must periodically report liveness | Identity protocol, trust model and capacity accounting |

See `job-lifecycle.md` for confirmed and undecided lifecycle behavior.
