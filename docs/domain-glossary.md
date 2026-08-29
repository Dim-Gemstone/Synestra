| Term | Meaning | Not | Lifecycle owner |
|---|---|---|---|
| `JobDefinition` | Registered executable work type | Execution instance | Application/domain |
| `Job` | Logical unit of submitted work | Single execution | Domain |
| `JobAttempt` | One execution try for a job | Retry policy itself | Domain |
| `Lease` | Time-bounded execution ownership | Permanent lock | Domain/application |
| `Worker` | Registered execution process with finite capacity | OS thread or HTTP request | Application/domain |
