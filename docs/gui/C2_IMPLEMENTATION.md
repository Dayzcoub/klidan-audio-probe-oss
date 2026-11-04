# C2 — Automatic device configuration scan

**Implementation status: partial, real query/Initialize only.** This slice adds the operator's one-button scan for one explicitly selected active Windows endpoint. It does not establish a working stream or a recommended AudioHost configuration. Physical QA was not run.

## C2.1 capability audit

| Capability | Current implementation |
|---|---|
| Real MMDevice enumeration and exact endpoint identity | Native worker, explicit GUI discovery; exact ID retained locally and revalidated before operations |
| Exclusive format query | Real worker `QueryFormatMatrix`; exact `IsFormatSupported` and period evidence; GUI supports it |
| Initialize-only | Real worker `ValidateFormatMatrix`; one candidate/period per request; GUI supports it with explicit consent |
| CLI `--test-stream` / `--test-duplex` | Real native CLI paths only; not connected to worker or GUI |
| CLI `--meter-capture` | Real native CLI Capture-only path; not connected to worker or GUI |
| Worker Capture/Render meter commands | Synthetic fake service only; excluded from real scan evidence |
| C1 profile v1 | Implemented local and sanitized profile contract/storage; extended here by aggregating existing real DTO evidence without a schema change |
| Real automated scan / stream / stability / paired Duplex | Not implemented beyond the single-endpoint query/Initialize scan described here |

The read-only AudioHost review in the C1 integration design identifies 48 kHz physical PCM16 candidates, stereo Render, mono/stereo Capture candidates, and a distinct internal Float32 processing path. Float32 internal processing does not make physical Float32 mandatory. AudioHost coordinated Exclusive also has requirements that a single endpoint Initialize result cannot prove. Shared remains the safe production default; Audio Probe does not change AudioHost settings or endpoints.

## Scan plan

The operator selects one exact, active Capture **or** Render endpoint from the current real MMDevice snapshot and presses **Сканировать**. There is no default-device fallback and no friendly-name matching. Capture and Render endpoints are not joined by name or guessed container identity. This version does not provide a combined pair scan; select either direction for a partial profile.

Before the worker receives an operation, one fresh confirmation names the endpoint, operation set, maximum duration, possible temporary Exclusive reservation, and confirms that no stream Start, PCM transfer, or output sound is planned. Consent fingerprints the endpoint ID, direction, and fixed plan. Endpoint change or worker disconnect invalidates confirmation. The worker still receives its existing one-use candidate receipt for each Initialize-only call; those receipts do not replace the scan-level consent.

Bounded candidates:

- Run the existing bounded passive matrix for Capture channel counts 1 and 2, or Render channel count 2 (44.1/48 kHz and PCM16/PCM24-in-32/Float32, with the existing paired period evidence). Candidate selection for active validation is restricted to consistent 48 kHz PCM16 rows.
- Initialize only exact candidates whose query row is consistent and `SUPPORTED_REPORTED`. Capture prioritizes stereo channel count 2, then mono channel count 1; each is tried `MINIMUM` then `DEFAULT`. Render checks channel count 2, `MINIMUM` then `DEFAULT`.
- At most four Initialize-only calls per scan. No 44.1 kHz, PCM24-in-32, Float32, or arbitrary channel sweep is actively initialized. Other existing query results remain available through Advanced diagnostics.
- GUI scan deadline is 180 seconds; the existing worker has a 30-second bounded response for each native query/Initialize call. Cancellation during a call can terminate the owned worker, leaving that operation outcome unknown. Earlier confirmed DTOs remain available; no response is converted to success.

For each supported candidate the worker returns its real Initialize HRESULT, alignment retry evidence, requested/actual period, buffer frames, event readiness, and release evidence. `RELEASE_ATTEMPTED` is not promoted to confirmed cleanup. Initialize success is not stream Start, serviced frames, audio observation, stability, Duplex, or physical QA.

## Result and profile

The result includes the complete returned query matrix and each accepted Initialize-only response. The profile producer aggregates all of these into one existing schemaVersion 1 `DeviceConfigurationProfile`; source provenance, exact local endpoint binding, timestamps, HRESULTs, and tested periods remain distinct. Existing strict local/sanitized serialization, validation, atomic save and overwrite refusal are reused. Save performs no hardware call.

No preferred or fallback configuration is emitted. The current evaluator requires real stream observation and stability evidence for a justified Probe recommendation; AudioHost eligibility additionally requires evidence this scan cannot provide, including coordinated Capture/Render requirements and production physical QA. Independent Capture and Render results do not imply Duplex.

Every scan reports Stream Start, serviced PCM/frame evidence, stability QA, Duplex QA, and KLIDAN physical QA as `NOT_RUN` / unavailable. Thus the scan-level result is `INCOMPLETE`, even when every selected Initialize succeeds. This deliberately prevents an Initialize-only workflow from being presented as a working audio configuration.

## Gaps before full C2

1. Extract or otherwise expose existing real CLI stream/Capture/duplex lifecycle through a worker-owned cancellable native service without routing PCM into the GUI or blocking the pipe reader.
2. Add explicit, separately confirmed Capture stream, Render-silence stream, and supported Duplex scan plans with exact endpoint binding, bounded duration and truthful device-loss/cleanup evidence.
3. Add exact pair selection and verified container-identity suggestions; never auto-pair by similar names. Duplex remains opt-in and must not create a feedback loop.
4. Add deterministic stream-evidence adapters and evaluator rules for started, serviced frames, faults, stability and Duplex; only then consider preferred/fallback and AudioHost eligibility.
5. Physical operator QA remains required for real device behavior. No physical operation was run in this implementation task.

## Validation

GUI Core tests use deterministic fake `IWorkerClient` DTOs and do not enumerate or activate audio hardware. They verify fresh consent and cancellation, exact endpoint query, bounded candidate selection, Initialize aggregation, no synthetic Start/stream/Duplex claims, and profile creation without recommendation. Native worker-process tests exercise existing protocol behavior only. Native CTest validates the existing CLI/unit paths; it does not constitute physical QA.
