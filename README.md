# Synestra

Synestra is a workload-agnostic distributed execution control plane for
coordinating background work across independently running workers.

Browser automation is the first concrete workload used to validate the core
execution model, but browser-specific concerns remain outside the control-plane
domain.

Frontend and product clients use the Client API. Independently running worker
agents use a separate logical Worker API and never access the control-plane
database directly.

The project is in an early design and implementation phase. Current lifecycle
semantics and unresolved decisions are documented explicitly and should not be
inferred from enum values or persistence structure alone.

## Documentation

- [Project brief](docs/project-brief.md)
- [Domain glossary](docs/domain-glossary.md)
- [Job lifecycle](docs/job-lifecycle.md)
- [Current project status](docs/project-status.md)
- [Testing and prerequisites](docs/testing.md)
- [Contribution conventions](docs/contributing.md)
- [Roadmap](docs/roadmap.md)
- [Architecture decision records](docs/decisions/README.md)
