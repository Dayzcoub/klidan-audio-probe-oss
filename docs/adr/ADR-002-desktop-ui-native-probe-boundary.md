# ADR-002 — Desktop UI ↔ Native Probe Core boundary

**Status: ACCEPTED — ARCHITECTURE A — OWNER APPROVED 2026-10-30**
**Date:** 2026-10-30
**Related baseline:** [GUI Design Kit v0.1, D0 approved](../gui/README.md)

## Context

The product currently has a native Windows C++ CLI. C1 Capture metering and C2 Render channel testing are implemented in code, but there is no GUI-facing service API or worker executable. The CLI entry point, argument validation, endpoint operations, stream orchestration, console presentation and mode dispatch are concentrated in `src/probe/wasapi_endpoint_probe.cpp`. Useful OS-neutral components exist, but the CLI functions are not already reusable services. The code review and proposed D1 boundary are documented in [D1 architecture](../gui/D1_ARCHITECTURE.md).

D0 approves visual and interaction requirements only. It does not choose a GUI framework, approve implementation, or change the need for explicit active-test consent.

## Candidates

### A. WPF + separate native worker + typed Named Pipe IPC — recommended

The WPF application owns presentation and launches a native worker for each app run. A versioned, bounded JSON protocol over a local Windows Named Pipe carries typed commands, events, results and immutable meter snapshots. The worker owns WASAPI/COM resources. The GUI owns the child process and pipe connection.

### B. WPF + native DLL with stable C ABI

The WPF application loads a native library and calls a versioned C ABI. The library would need explicit thread, COM, object lifetime, cancellation and callback/snapshot rules. Native faults and hangs would share the WPF process boundary.

## Accepted architecture

The owner approved **A** on 2026-10-30: WPF (.NET 10) desktop application, separate native C++ Probe Worker, versioned typed IPC over local Windows Named Pipes, WASAPI/COM owned exclusively by the worker, and continued CLI support. The existing code has useful portable headers and test seams, but no stable service API; much orchestration and `printf` presentation remains in the CLI translation unit. A worker lets the C++ layer retain audio ownership, gives the UI a versioned typed boundary, and contains most native process failures. Named Pipe overhead is proportionate to command/event traffic and 10–20 Hz small meter snapshots.

Candidate B is not recommended for the first GUI because it cannot isolate access violations, deadlocks or a blocked native call from the UI process, and the current code has no stable GUI ABI. It remains a possible later option if measured IPC overhead or packaging constraints justify a re-review and the ownership/failure contract is demonstrably safe.

## Decision

**ACCEPTED — Architecture A — owner approved 2026-10-30.** The decision is WPF (.NET 10) with a separate native C++ Probe Worker, versioned typed IPC over local Windows Named Pipes, WASAPI/COM owned exclusively by the worker, and continued CLI support. This accepts the boundary and technology choice; proposed shutdown timeouts, exact IPC schemas, deployment details, and runtime behavior have not passed implementation validation. No GUI, worker, or IPC implementation is included in this D1 approval commit.

## Consequences

- Keep the CLI supported; both hosts should call the same typed Probe services, never scrape console text.
- Extract a service boundary incrementally from existing code; do not treat `wmain`, `TestInitialize` or `RunStream` as safe public APIs.
- Keep WASAPI/COM ownership in the worker and keep JSON/pipe work off audio servicing threads.
- Preserve local endpoint identity only for local selection and execution; exported reports remain sanitized.
- D2 may build a WPF shell against fake client data only. Native worker/pipe integration and active device access remain later slices.

## Rejected for this phase

- **WPF parsing existing CLI stdout/stderr:** console strings are presentation, not a versioned contract; wording changes and interleaved worker output make it unsafe as an API.
- **WPF calling a native DLL as the first boundary:** lower transport overhead does not offset the lack of process failure isolation and the lifetime/threading surface visible in the current implementation.
- **Always-running service/daemon:** unnecessary for a manually launched diagnostic utility and creates background lifetime and endpoint-ownership risks.
- **Automatic restart after worker failure:** could repeat a disruptive Exclusive operation without fresh operator consent.

## Related D1 documents

- [Architecture and code review](../gui/D1_ARCHITECTURE.md)
- [Named Pipe contract](../gui/D1_IPC_PROTOCOL.md)
- [Lifecycle and shutdown](../gui/D1_LIFECYCLE.md)
- [Test strategy](../gui/D1_TEST_STRATEGY.md)
