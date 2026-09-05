# Execution Scenarios

These reference scenarios describe product requirements, not implemented
features. ADR-0008 records the accepted execution boundary. Provider names and
stage examples do not prescribe public contracts or core domain entities.

## Download Video or Parse File

A client supplies a video source or a file input. A Worker downloads the video
from a provider such as YouTube, parses a PDF, processes an image, or performs
another workload-specific transformation.

The Job completes with an execution outcome and a workload-specific result.
Large outputs such as videos or images are exposed through artifact references.
The control plane does not interpret the internal processing algorithm.

Open details include input transfer, artifact storage and publication, partial
download cleanup, output limits, and when an output is considered complete.

## Setup Account

A client supplies account configuration and the authentication information needed
by the workload. A handler obtains a clean browser context, potentially from a
pool, or uses an external API. It authenticates, sets a username, avatar, and other
settings, verifies the outcome, and returns feedback.

The browser implementation and its restart or pooling behavior belong to the
workload runtime. Internal browser recovery does not itself define a new attempt.
Example stages are authentication, updating settings, and verification.

Open details include secure credential delivery, account isolation, context
cleanup, required versus optional changes, and whether partially applied settings
constitute business success. Cancellation does not roll back completed changes.

## Send Letters

A client supplies inputs and one or more sets of authentication information. The
handler obtains a clean browser context or API session, authenticates, checks
preconditions, and processes an input-driven loop. It may call other APIs or tools.
The expected scenario is predominantly asynchronous I/O, although other workloads
may require substantial CPU or memory resources.

The user can observe the current stage, counters, and available partial results,
and request cooperative cancellation once that feature is implemented. Final
feedback describes the work performed. Exact success criteria for mixed outcomes
remain open.

Pause is deferred. The current candidate keeps execution alive in the same
process. Durable continuation after losing that process remains an open question.
A progress counter alone is not enough to resume safely: element identity and
confirmed or uncertain external effects matter.

## Shared Failure and Control Requirements

| Situation | Accepted direction | Decision still required |
|---|---|---|
| Handler completes | Publish outcome and workload-specific result | Output contract and publication transaction |
| User cancels running work | Stop cooperatively at a safe point; preserve available partial results | State transitions, races, and unresponsive handler behavior |
| Handler fails | Preserve execution history; no initial automatic retry | Error categories and Job/attempt final states |
| Worker is lost | Eventually record lost execution; no initial automatic rerun | Detection, lease expiration, late reports, and state transitions |
| API response is lost | Do not assume whether a request committed | Submission and Worker protocol idempotency |
| External effect occurs before progress is saved | Outcome may be uncertain | Workload reconciliation and future retry safety |
| Pause requested | Deferred from first execution slice | Live-process pause protocol and resource accounting |
| Resume after restart requested | No guarantee accepted | Checkpoint contract, compatibility, session recovery, and attempt identity |

## Design Questions Before Implementation

- Which input/output contracts and versions does each handler support?
- Are referenced inputs immutable across attempts?
- How are credentials delivered without exposing them in ordinary history?
- Can concurrent Jobs use the same account or other exclusive resource?
- What limits apply to execution duration, result size, and progress frequency?
- Which reports are retained per attempt, and how is the current Job view selected?
- What constitutes success when only part of a batch succeeds?

Future retries must distinguish repeating the same logical work from advancing a
successful loop iteration. Durable Workflow orchestration may later compose these
Jobs, but their internal stages do not require a workflow engine.
