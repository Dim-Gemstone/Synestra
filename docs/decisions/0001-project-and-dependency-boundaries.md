# ADR-0001: Project and Dependency Boundaries

**Status:** Accepted

## Context

Synestra is being developed as a distributed execution control plane with explicit domain concepts such as jobs, execution attempts, leases, workers, and worker capacity.

The system already contains domain and persistence concerns and is expected to gain an Application layer and HTTP API behavior.

Without explicit dependency boundaries, infrastructure concerns such as Entity Framework Core, PostgreSQL, ASP.NET Core, or Aspire could gradually leak into the domain model. Likewise, orchestration logic could accumulate inside API endpoints or persistence code.

The project should preserve a domain-oriented structure without introducing architectural abstractions that are not justified by concrete use cases.

## Decision

Synestra will use the following logical dependency direction:

```text
API
 ↓
Application
 ↓
Domain
```

Persistence is an infrastructure concern and depends on Domain:

```text
Persistence
    ↓
  Domain
```

The API project acts as the application composition root and may therefore reference both Application and Persistence:

```text
             ┌─────────────┐
             │     API     │
             └──────┬──────┘
                    │
             ┌──────┴──────┐
             ▼             ▼
       Application     Persistence
             │             │
             └──────┬──────┘
                    ▼
                  Domain
```

The boundaries have the following responsibilities.

### Domain

Domain contains:

- domain entities;
- value objects where useful;
- domain invariants;
- state-transition behavior;
- domain-specific types and enums.

Domain must not depend on:

- Entity Framework Core;
- PostgreSQL;
- ASP.NET Core;
- Aspire;
- API contracts;
- application orchestration;
- workload-specific infrastructure such as CefSharp.

Domain entities must not expose public setters solely for persistence convenience.

### Application

Application contains use-case orchestration.

It is responsible for coordinating domain behavior for operations such as:

- submitting jobs;
- registering or updating workers;
- claiming work;
- renewing leases;
- completing attempts;
- failing attempts;
- cancelling jobs.

Application defines transaction boundaries at the use-case level.

The exact abstraction between Application and Persistence is not fixed by this ADR and should emerge from concrete use cases.

### Persistence

Persistence contains:

- EF Core configuration;
- `DbContext`;
- PostgreSQL-specific persistence logic;
- database queries;
- database concurrency mechanisms;
- implementation of persistence abstractions when such abstractions are justified.

Persistence must not move infrastructure concerns into the Domain model.

### API

API is responsible for:

- transport;
- request and response contracts;
- authentication and authorization when introduced;
- input translation;
- composition and dependency injection;
- exposing Application use cases.

API endpoints must remain thin and must not become the primary location for domain workflow logic.

## Architectural Approach

Synestra follows a pragmatic domain-oriented architecture.

The following patterns are not mandatory by default:

- generic repositories;
- MediatR or equivalent mediator infrastructure;
- full CQRS separation;
- domain events;
- specification pattern;
- generic unit-of-work abstractions.

They may be introduced later only when they solve a concrete recurring problem.

## Consequences

### Positive

- Domain behavior remains independent of infrastructure.
- Core execution semantics can be tested without ASP.NET Core or PostgreSQL where appropriate.
- API remains replaceable as a transport boundary.
- Browser-specific execution logic cannot define the core architecture.
- Architectural complexity is introduced only when justified.

### Trade-offs

- API may reference both Application and Persistence because it acts as the composition root.
- Some persistence-sensitive use cases may require carefully designed interfaces or application/persistence collaboration.
- The architecture intentionally does not prescribe every implementation pattern in advance.

## Not Decided Here

This ADR does not define:

- aggregate boundaries;
- repository abstractions;
- exact transaction implementation;
- job claim concurrency semantics;
- retry behavior;
- lease expiration semantics;
- Application project folder structure.
