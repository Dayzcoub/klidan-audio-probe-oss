# C3.1 — Headless Scan Coordinator

## Scope and flow

`ScanPlan.Build` snapshots the selected exact Windows endpoint IDs, explicit pair identity, endpoint directions/states, available operation capabilities, and bounded settings into a deterministic fingerprint. Friendly-name similarity never establishes Capture/Render membership. A single endpoint is a valid partial scan. Duplex is planned only for two distinct active endpoints explicitly selected as a pair. Capture path (analogue vs driver loopback) remains unknown unless the caller supplies that fact.

`ScanCoordinator.RunAsync` allows one run per coordinator instance and executes a sequential scan: fresh discovery, per-endpoint Query, supported-candidate selection, Initialize, eligible stereo Stream, eligible pair Duplex, evidence evaluation, then Save. Candidate selection is bounded to 48 kHz PCM16, Capture mono/stereo and Render stereo, using only exact supported query rows. MINIMUM and DEFAULT are independent. Operation count and per-operation deadlines are bounded. No Exclusive operations overlap. Events carry increasing sequence numbers, stage, lifecycle, endpoint and operation identity; Duplex operations retain both exact endpoint IDs.

Every operation ends as `PENDING`, `RUNNING`, `COMPLETED`, `SKIPPED`, `FAILED`, or `UNKNOWN`. A safely cleaned candidate failure is retained and later candidates may run. Cancellation/deadline invokes the adapter’s bounded cancellation path. Stale identity, device loss, worker disconnect, or unverified cleanup stops subsequent operations. Earlier accepted evidence remains in the result. A completed coordinator run means its bounded plan ended; it does not mean each test passed.

The adapter is the only execution seam and reports capabilities, exact operation evidence, current identity/device state, worker state, and cleanup safety. C3.1 originally permitted only `DeterministicFake`; C3.3 adds a real Worker adapter gated on an already registered Worker session bound to the same discovery snapshot and immutable plan. C3.4 connects it to a consent-gated one-click GUI workflow. Synthetic evidence retains its `TEST_FAKE_*` provenance and cannot produce a real recommendation or profile. `Physical RTL` remains `NOT_MEASURED`; AudioHost remains `NOT_CERTIFIED`.

## Current operations and gaps

The repository has individual real Worker operations for endpoint discovery, Exclusive Query, Initialize-only, bounded Stream, and Duplex. The coordinator calls their existing session-scoped APIs through `RealWorkerScanExecutionAdapter`; it adds no WASAPI path and does not mint or reuse their manual one-use consent receipts. Worker session authorization is registered by a caller only after explicit approval.

For real Worker runs, `SAVE` aggregates accepted endpoint evidence and pair Duplex evidence, validates and atomically saves profile(s), then writes and reloads a bounded local scan summary. The deterministic fake adapter still skips persistence. Profile v3 and embedded Duplex report v2 remain unchanged.

### Profile GUID casing prerequisite

C3.2 fixes the C2c.4 casing defect: `CreateDuplex` parses the DTO and embedded report operation IDs and compares `Guid` values. It also validates the report's exact endpoint pair and immutable 48 kHz stereo PCM16 settings before creating profile v3. Raw embedded report text remains intact as accepted evidence.

## Remaining one-click follow-up

1. C3.4 adds the operator consent/progress/result UI and preserves the exact pre-authorization discovery snapshot.
2. Physical operator QA is still required; automated injected-worker scans do not establish real device behavior.
3. No audio engine rewrite is required. Physical RTL and AudioHost certification remain outside this scan.

## Validation boundary

C3.1 automated coverage uses deterministic adapters and synthetic reports. It validates sequencing, evidence binding, failure continuation, cancellation, bounded cleanup decisions, progress, and no physical RTL/AudioHost authorization. It is not a physical-device scan and does not establish current hardware behavior.
