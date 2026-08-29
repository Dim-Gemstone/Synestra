# ADR-0004: Browser Automation as the Initial Workload

**Status:** Accepted

## Context

The original ideas that eventually led to Synestra were strongly influenced by browser automation and CefSharp/CEF-based systems.

Those scenarios include potentially:

- managing multiple browser instances;
- executing browser automation remotely;
- grouping or pooling browsers;
- running browser workloads on dedicated workers;
- exposing browser execution through a backend control plane.

However, directly designing Synestra as a CefSharp browser manager would tightly couple the execution model to one workload and make otherwise general concepts such as workers, attempts, leases, retries, and capacity browser-specific.

The current domain model has evolved into a more general distributed execution system.

A concrete workload is still necessary to validate whether that model is useful in practice.

## Decision

Browser automation, initially based on CEF/CefSharp, will serve as the first concrete Synestra workload.

Synestra Core remains workload-agnostic.

Browser-specific concerns belong in worker-side or workload-specific projects and must not become fundamental control-plane domain concepts unless they are independently justified.

The relationship is:

```text
              Synestra Control Plane
                      │
             generic execution model
                      │
        ┌─────────────┴─────────────┐
        │                           │
 Browser Worker              Future Worker Type
  CEF/CefSharp                 other workload
```

## Core Concepts Must Remain Generic

The following concepts are generic Synestra concepts:

- `JobDefinition`;
- `Job`;
- `JobAttempt`;
- `Lease`;
- `Worker`;
- worker capacity;
- heartbeat/liveness;
- execution result;
- retry/recovery semantics.

The core should not require concepts such as:

- browser tabs;
- CefSharp controls;
- WinForms handles;
- DOM-specific state;
- browser rendering surfaces;
- browser profiles;
- website-specific automation logic.

Such concepts may exist inside browser-worker implementations.

## Browser Worker

A browser worker may eventually be responsible for:

- maintaining one or more browser instances;
- executing browser-specific jobs;
- reporting worker capacity;
- reporting liveness;
- accepting temporary execution leases;
- returning execution results.

Whether one Synestra capacity unit corresponds to one browser instance is not decided by this ADR.

## Rendering

Synestra does not currently require the browser workload to support a specific rendering model.

The following remain open or deferred:

- headless-only execution;
- native local rendering;
- remote rendering;
- interactive remote access;
- multiple rendering modes.

These choices must not influence the generic job execution model prematurely.

## Browser Pools and Groups

Browser pools, worker groups, affinity, or similar concepts may be introduced later if real browser workloads demonstrate the need.

They are not part of the initial Synestra core model.

## Consequences

### Positive

- Synestra is tested against a concrete and non-trivial workload.
- Browser automation provides meaningful requirements for leases, capacity, failures, and long-running work.
- Core architecture remains reusable for non-browser execution.
- CefSharp and WinForms dependencies remain isolated from the Domain.

### Trade-offs

- Some useful browser-specific optimizations cannot be promoted into the core until their generality is demonstrated.
- The generic execution model must avoid becoming either too browser-specific or unnecessarily abstract.
- Worker capability modeling may require future extension when multiple workload types exist.

## Rejected Direction

Synestra will not recreate the previous desktop browser application architecture one-to-one.

In particular, the control plane is not defined around:

- users directly owning UI tabs;
- WinForms browser controls;
- one process containing both orchestration and browser UI;
- CefSharp as a core dependency.

Useful execution scenarios from previous browser applications may be retained without retaining their application architecture.

## Not Decided Here

This ADR does not define:

- browser worker protocol;
- headless versus rendered execution;
- browser instance lifecycle;
- browser profile persistence;
- remote browser streaming;
- worker grouping;
- workload capability negotiation;
- capacity semantics for browser workers.
