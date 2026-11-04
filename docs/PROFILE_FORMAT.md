# JSON report and device-profile contract

**Status:** WASAPI Exclusive format-matrix diagnostic report v1 and the Audio Probe Device Configuration Profile v1 foundation are implemented. KLIDAN profile import/application and compatibility certification are not implemented. See [C1 implementation and AudioHost mapping](gui/C1_IMPLEMENTATION.md).

## Separate concepts
- **Report:** what one specific invocation actually observed, with each check's exact result and provenance.
- **Profile:** advisory, device-configuration-specific recommendation derived from accepted reports. Never directly executed without revalidation.
- **Reference hint:** public ALSA/ASIO/vendor data; helpful but *not* proof of Windows WASAPI behavior.

## Device Configuration Profile v1

The machine-readable JSON Schema is [`schemas/device-configuration-profile-v1.schema.json`](schemas/device-configuration-profile-v1.schema.json). Audio Probe creates a validated machine-local profile from current accepted real endpoint/query results and optional matching Initialize-only evidence. The schema separates metadata, exact local endpoint bindings, per-operation evidence, AudioHost assessment, and recommendation. It records independent Capture, Render, and Duplex evidence references; partial one-direction profiles are valid. Current C1 output never recommends a mode because format support or Initialize does not establish stream stability or physical QA.

The compatible v3 pair-profile schema is [`schemas/device-configuration-profile-v3.schema.json`](schemas/device-configuration-profile-v3.schema.json). It adds exact Capture/Render pair bindings, one or two accepted `REAL_WASAPI_DUPLEX` observations, and an evidence-bound `PROBE_PROVISIONAL` recommendation. It does not imply AudioHost eligibility. Valid v1/v2 profiles remain readable; they are not implicitly merged into a pair. See [C2c implementation](gui/C2C_IMPLEMENTATION.md) and the [Duplex report v1 schema](schemas/wasapi-duplex-validation-v1.schema.json).

The v4 sustained profile schema is [`schemas/device-configuration-profile-v4.schema.json`](schemas/device-configuration-profile-v4.schema.json). It adds an explicit sustained-selection section containing bounded references to accepted `REAL_WASAPI_SUSTAINED_DUPLEX` evidence. Only accepted 300- or 600-second clean evidence can produce a sustained preferred or independently larger fallback configuration; 60-second screening and v3 five-second observations remain insufficient. V4 keeps `AudioHostEligibility` as not certified and `PhysicalRTL` as not measured, and sanitized exports remain unbound.

Local profiles are stored under `%LOCALAPPDATA%\KlidanAudioProbe\Profiles`; writes are size-bounded and use no-overwrite atomic creation. Sanitized serialization removes endpoint IDs and names and marks bindings unbound. It cannot authorize a local endpoint. The parser enforces schema version, duplicate-key rejection, unknown-field rejection, bounds, state consistency, endpoint direction, evidence references, and recommendation eligibility without hardware access.

The older proposal below remains background for diagnostic reports; it is not the Device Configuration Profile contract:

## Proposed report fields
- `schemaVersion`: integer; reject incompatible major version.
- `reportId`, `timestampUtc`, `probeVersion`, `platform`, `backend`.
- `device`: vendor/model/firmware/driver version/USB mode (when actually known); **local endpoint/container IDs excluded from public export**.
- `endpoints[]`: `direction`, friendly label, physical `channels` from actual format, sample rate, container and valid bits, format tag/subtype, channel mask.
- `candidates[]`: format tested; `isFormatSupportedHr`, `initializeHr` (nullable), `startHr` (nullable); actual buffer frames and period (nullable), duration, faults/drops/frames (nullable).
- `latency`: distinguish OS-reported stream latency, loopback RTL, ring capacity and ring depth, with explicit methods.
- `evidence`: `sourceType` (passive/active/physical/third-party), `sourceRef`, driver/firmware context, measurement date; `klidanVerified` default false.
- `privacy`: sanitized vs local-diagnostic.

## Implemented: Exclusive format-matrix report v1

`--scan-formats` writes `evidence.sourceType: "PASSIVE_QUERY"`; `--validate-formats` writes `"INITIALIZE_ONLY"`. Both produce `reportType: "wasapi-exclusive-format-matrix"` with `schemaVersion: 1`, timestamp, probe version, backend, nullable unknown device metadata, direction, evidence and sanitized privacy classification. `reportId` is a random UUIDv4 generated from the Windows system RNG and is independent from `timestampUtc`. `klidanVerified` and `certified` are always false. Raw endpoint IDs, endpoint labels and the requested output path are omitted.

Each of the 24 candidate/period rows records direction, sample rate, channels, encoding/container/valid bits, period mode and one of `SUPPORTED_REPORTED`, `INITIALIZED`, `READY`, `UNSUPPORTED`, `FAILED`, `NOT_TESTED` or `INCOMPLETE`. Exact `IsFormatSupported` HRESULT and its status are separate from Initialize results; a query cleanup problem does not erase successful Initialize evidence. An attempted Initialize with missing final/retry evidence is `INCOMPLETE`; no Initialize call is `NOT_TESTED`. The report preserves nullable first/final Initialize HRESULTs, alignment retry geometry/results, requested and actual period, actual buffer frames, event creation/SetEventHandle readiness, and query/attempt cleanup results. COM `Release()` calls are represented by release-attempt counts/status, never synthetic HRESULTs; `cleanupHr` is null and `eventHandleCloseHr` records the verifiable event close result. `startHr` is null, `startCalled` is false, and `audioTransferred` is false for every candidate.

HRESULTs are serialized as eight-digit hexadecimal strings; unknown HRESULTs and measurements are JSON `null`. `actualPeriod100ns` is derived from the initialized buffer frames and sample rate. This report records diagnostics only; it is not a compatibility profile or formal hardware certification.

## Illustrative schema fragment — not data from hardware
    {
      "schemaVersion": 1,
      "probeVersion": "0.1.0",
      "backend": "wasapi",
      "device": {"manufacturer": "EXAMPLE", "model": "NOT MEASURED", "driverVersion": null, "usbMode": null},
      "endpoints": [],
      "candidates": [],
      "evidence": {"sourceType": "design-example", "klidanVerified": false},
      "privacy": "sanitized"
    }

All unmeasured fields should be `null` / omitted, never copied from a different interface, API or OS. Drivers may expose **Windows endpoints** differently from physical mixer channel strips; report the source of channel topology.

## Device identity and trust
- Same model name alone is insufficient; driver version, firmware and selected USB mode may change formats/periods.
- No publicly committed serial number, raw endpoint ID, Windows user name, machine-specific path or microphone recording.
- Profile status should say reference, observed/passive, tested/active, operator-QA, or KLIDAN-verified; avoid ambiguous `verified: true` without scope and evidence.
- Foreign/third-party profiles must not silently activate Exclusive or set system audio preferences.

Before implementation, write JSON schema plus validation/unit tests and only then version the contract for use by KLIDAN.
