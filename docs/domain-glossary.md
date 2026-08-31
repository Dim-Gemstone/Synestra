# Domain Glossary

This glossary defines the shared language of Synestra. It intentionally does
not assign lifecycle ownership or aggregate boundaries that have not yet been
decided.

| Term | Meaning | Not | Confirmed semantics | Still undecided |
|---|---|---|---|---|
| `JobDefinition` | Registered type of executable work | A submitted execution instance | Has a stable UUID identity and a unique immutable machine-readable type; can be enabled or disabled | Who manages definitions and how disabling affects existing jobs |
| `Job` | One logical unit of submitted work | One physical execution | References its `JobDefinition` by ID, snapshots its type, can have multiple attempts and preserves their history | Exact status derivation, cancellation and retry semantics |
| `JobAttempt` | One execution try for a Job | The retry policy itself | Belongs to one Job and records one execution outcome | Exact creation point and state after lease expiration |
| `Lease` | Time-bounded exclusive right for a Worker to execute an attempt | A permanent lock or execution result | Has acquisition and expiration times and cannot permanently assign work | Renewal, expiration recovery and late-result behavior |
| `Worker` | Registered worker agent with identity, liveness and finite capacity | An OS thread, HTTP request, browser instance or individual CEF subprocess | May disappear and must periodically report liveness | Identity protocol, trust model, process lifetime and capacity accounting |

## Working execution terminology

The following terms describe the current topology without fixing process
lifetime or deployment details.

| Term | Meaning | Boundary |
|---|---|---|
| Worker agent | Process that communicates with the Worker API, reports liveness and capacity, and supervises execution | Does not access the control-plane database directly |
| Execution process | Process or process tree that performs a particular workload under worker supervision | Does not own queue coordination semantics |
| Client API | Product-facing API used by the frontend and other clients | Expresses user and product use cases |
| Worker API | Machine-to-machine API used for worker coordination | Expresses registration, work acquisition, lease, and result protocol |

For a browser workload, one execution process may supervise or contain a tree
of CEF subprocesses. Those subprocesses are workload implementation details,
not individual Synestra Workers.

See `job-lifecycle.md` for confirmed and undecided lifecycle behavior.
