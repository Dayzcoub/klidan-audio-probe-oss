# C3.4 — One-click scanner

## Operator flow

The Windows device page now provides the normal `Устройство → Сканирование → Результат` route. The operator explicitly refreshes Windows devices (which connects the existing GUI-owned Worker when it has not yet been started), selects one exact active endpoint, and presses **Сканировать**. A Capture/Render pair is optional and must be selected explicitly; endpoint names are never used to infer physical membership. A single endpoint produces a partial scan.

Before any scan operation, the app performs one fresh Windows endpoint discovery and verifies the selected endpoint IDs, directions and active state. It freezes the `ScanPlan` and Worker plan from that same snapshot, then presents one consent containing endpoint identity, planned operations/settings, duration/session limits, Exclusive-access impact, Capture discard behavior, silent Render behavior, and exclusions for tone, physical routing and RTL. The Worker scan authorization is registered only after the operator accepts. No second discovery occurs between registration and execution.

The existing `WorkerAutomaticScanWorkflow` composes the same `NativeWorkerClient`, `RealWorkerScanExecutionAdapter`, and `ScanCoordinator`; it does not implement WASAPI or duplicate candidate selection. Planned operations run sequentially. The screen presents stage progress, skipped/failing operations, a bounded stop request, outcome, recommendation limits, and automatic persistence paths. A cancellation request is not displayed as completed until the operation/session response establishes its outcome. Unresolved execution or cleanup is `UNKNOWN` and prevents reuse in the current app session.

For Stream operations, the authorized scan `operationId` is correlated across the immutable plan, Worker request/session authorization, IPC response and coordinator operation. The embedded native CLI `reportId` is a separate report UUID: it must parse as a nonempty UUID, but is not required to equal the operation UUID. Duplex follows the same separation: its report UUID is independent while its embedded operation UUID remains correlated to the authorized scan operation.

The GUI keeps exact endpoint selection and the pre-registration discovery snapshot for the whole run. If an endpoint disappears, becomes inactive, the Worker disconnects, or identity changes, there is no fallback selection. Previously accepted evidence remains attached to the returned scan result. `SAVE_FAILED` and its error are surfaced; successful profile and summary paths are displayed after the existing save/read-back validation.

Detailed single-operation Query, Initialize, Stream, Duplex and profile-save controls remain available only under **Developer Tools**. The standard scan does not ask the operator to choose period, repeat tests, or save manually.

## Test boundary

The local IPC integration suite exercises the production automatic workflow/coordinator against an injected Worker backend. Its responses are synthetic test fixtures and are explicitly checked not to produce real profiles, physical RTL evidence or AudioHost certification. GUI Core tests verify selection, preparation/consent, cancellation, progress, result persistence presentation, start/stop state, non-concurrency, and Developer Tools isolation. These fixtures do not establish WASAPI behavior.

## Safety and remaining QA

- Real operations remain bounded by the Worker session authorization and existing native operation limits. The coordinator serializes Exclusive work.
- A driver call already blocked inside COM may not be interruptible. The GUI keeps the stop action available but reports the result as unknown unless bounded Worker/session cleanup is actually observed.
- Capture packets are discarded; raw PCM is not recorded. Render submits silence. No physical loopback, tone, acoustic RTL, or AudioHost activation is performed.
- Physical RTL remains `NOT_MEASURED`; AudioHost remains `NOT_CERTIFIED`.
- No physical endpoint scan was run as part of this implementation. Before normal Audient use, an operator must verify endpoint selection, read the consent, run the bounded scan, and inspect each saved result and profile. Interactive DPI/layout QA also remains an operator check.

## Audient iD4 operator QA procedure

1. Launch the Release GUI. On the Windows Devices screen, use the explicit refresh action and confirm the Worker status is connected and the Windows discovery is current.
2. Select the active Audient iD4 Capture endpoint by its exact endpoint row. If testing Duplex, enable explicit pairing and select the intended Audient Capture and Render endpoints independently; do not rely on matching names. If either endpoint is uncertain, run a single-direction partial scan or stop.
3. Press **Сканировать**. Review the exact IDs, operations, formats/periods, maximum durations, Exclusive reservation notice and Capture/Render behavior in the consent. Accept once only if the plan is correct.
4. Observe the stage list. If stopping, request cancellation and wait for the reported terminal state; do not assume a blocked driver call has already stopped.
5. Review the outcome, preferred/fallback scope, limitations, and the displayed scan-summary/profile paths. Confirm saved files were produced by the app and retain `AudioHost: NOT_CERTIFIED` and `Physical RTL: NOT_MEASURED` as limitations.

This procedure is for operator QA only. Codex validation uses the injected fake Worker and does not touch the Audient or any physical endpoint.
