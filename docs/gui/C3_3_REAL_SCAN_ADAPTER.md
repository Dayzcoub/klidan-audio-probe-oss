# C3.3 — Real Scan Adapter and Persistence

## Implemented boundary

`RealWorkerScanExecutionAdapter` implements the existing `IScanExecutionAdapter` seam. It is constructed only with an already connected `NativeWorkerClient`, the typed discovery snapshot captured before authorization, the opaque session token returned by `RegisterScanSessionAsync`, the exact Worker plan registered with that token, and the matching frozen `ScanPlan`. Its `IsBoundTo` check rejects a changed plan, missing exact endpoint binding, non-active endpoint, stale/disconnected worker, incompatible capability set, or a Worker operation limit broader than the coordinator approval.

The required ordering is:

1. The caller explicitly invokes `ListWindowsEndpointsAsync` and retains that response.
2. The caller chooses exact active endpoint IDs and builds `ScanPlan` and `RealWorkerScanExecutionAdapter.CreateWorkerPlan`.
3. A real UI consent surface must show the immutable operations, endpoints, limits, and settings and return a fresh approval. **C3.3 does not implement this surface and normal application users cannot initiate this flow.**
4. The caller registers that approved Worker plan once, then constructs the adapter with the returned opaque token and the same pre-registration discovery response.
5. `ScanCoordinator` checks the snapshot locally at `Discovery`; it does not enumerate again because Worker discovery refresh invalidates session authorization.
6. Query, Initialize, Stream, and Duplex execute sequentially through the existing scan-scoped Worker APIs. Stream/Duplex use dedicated typed methods that do not accept manual-consent DTOs. Worker/native operations and report validation remain authoritative.
7. The coordinator closes the idle Worker session, evaluates only accepted reports, and persists one aggregated pair profile v3 when valid Duplex evidence exists, plus a bounded scan summary. That profile includes accepted Capture/Render query, Initialize, stream, and Duplex evidence from this scan. Without valid Duplex evidence, available single-endpoint evidence is saved in the existing single-endpoint profile format. Cancellation invalidates the session and awaits the in-flight request; an unresolved driver operation remains `UNKNOWN` and latches the coordinator against another run.

The deterministic IPC fixture uses the same adapter path but reports `TEST_FAKE_*`. Adapter mode is derived from the Worker fixture capability; the coordinator accepts only that test provenance in fixture mode. Storage rejects fake/unknown provenance and is never invoked for fake scans.

## Bounds and failure behavior

The Worker plan keeps a 600-second maximum session lifetime, 24 operation maximum, and the scan’s 1–10 second stream/Duplex duration bound. The adapter computes a conservative upper bound from the maximum Query/Initialize/Stream/Duplex calls and their current Worker response deadlines; plans exceeding the session lifetime are rejected before registration. Coordinator per-operation timeout defaults to 60 seconds so a bounded 5-second Duplex request can complete within the existing 51-second Worker response deadline.

No second discovery is run. The Worker continues to revalidate exact endpoint IDs/directions at each active operation. Unsupported or failed candidates remain in their structured matrix and safely cleaned operations can continue. Transport loss, stale endpoint identity, malformed evidence, unresolved calls, or uncertain cleanup stop later operations. A blocked COM Query/Initialize cannot be safely interrupted; cancellation invalidates the Worker session but the coordinator waits only its bounded cleanup window and then returns `UNKNOWN` if the call has not settled.

## Automatic local persistence

When accepted real Duplex reports exist, the scan creates one profile v3 bound to the exact Capture/Render pair. It combines the accepted per-endpoint query, Initialize, and stream evidence with the pair's Duplex observations and references. One period is sufficient; no second result is fabricated. Without Duplex evidence, available evidence is saved in the existing single-endpoint profile format. Every profile is validated by its existing factory/serializer, saved with the existing atomic no-overwrite storage method, and parsed back from disk. The default profile directory remains `%LOCALAPPDATA%/KlidanAudioProbe/Profiles`; scan summaries are stored under `%LOCALAPPDATA%/KlidanAudioProbe/Scans`.

Summary schema v1 is bounded to 8 MiB, contains stage/lifecycle/operation identity/cleanup, recommendation, exact local target binding, profile references, `PhysicalRtl=NOT_MEASURED`, and `AudioHostEligibility=NOT_CERTIFIED`. It stores no duplicate full evidence payloads. It is written with atomic create-new semantics and parsed back after save. Interrupted scans with accepted evidence attempt the same persistence path. If persistence fails, `ScanRunResult.Persistence.Status` is `SAVE_FAILED`, the Save operation fails, and the scan is not reported as fully successful. Previously created valid profile files are retained if a later summary write fails.

## Compatibility and unimplemented work

- Profile v1/v2/v3 contracts and embedded Duplex report v2 are unchanged.
- No native WASAPI engine or quality rules changed.
- The adapter still never manufactures operator approval. C3.4 adds the GUI consent surface and a thin workflow that registers the Worker plan only after acceptance, then invokes this adapter with the same pre-registration discovery snapshot.
- C3.4 adds UI progress/result/persistence presentation. Physical operator QA remains required before treating the normal workflow as hardware-validated.
- No automated test accesses a physical endpoint. Operator physical QA remains required before normal-user scans are enabled.
- Physical RTL remains `NOT_MEASURED`; AudioHost remains `NOT_CERTIFIED`.

## Validation

Automated coverage added for end-to-end coordinator execution over a local injected Worker process and for real-shaped profile v3 plus incomplete-summary persistence in isolated temporary directories. Those fake Worker runs are explicitly synthetic and are asserted not to create hardware profiles. Build/test results are recorded in the C3.3 delivery report.
