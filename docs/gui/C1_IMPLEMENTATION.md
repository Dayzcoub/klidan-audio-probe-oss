# C1 — Device Configuration Profile foundation

**Status:** profile v1 contract, strict local validation, result projection, local save, and sanitized serialization implemented in Audio Probe. Full scan orchestration, AudioHost import, recommendations, and physical QA remain future work.

## Current AudioHost configuration map (read-only audit)

Audit reference: an upstream private design source at a recorded historical revision. No upstream repository was changed.

| AudioHost input | Current behavior and values | Change/restart/verification/fallback/safety |
|---|---|---|
| Transport mode | `OperatorSettingsSnapshot.AudioHostMode`: `Shared` (legacy persisted default) or `CoordinatedExclusive`. `AudioHostLaunchEnvironment.Configure` maps these to `portaudio` or `native-wasapi-coordinated-exclusive` for Master and Voice. Native direct `native-wasapi-exclusive` is an implementation transport, not another operator preference. | Persisted in operator settings. `App.AudioHostMode.ApplyAudioHostModeAsync` requires quiescent Program/Video/Voice, stops and restarts the host, then checks reported Master/Voice transport. No mode switch during playback; no automatic transport fallback. KLIDAN's target is profile-qualified Exclusive for professional devices; Shared is a separate home/media fallback. This Probe profile work does not change current AudioHost defaults or activate a profile. |
| Program / MASTER endpoint | `ProgramOutputEndpointId` is an exact Windows endpoint identity. The catalog resolves it; unavailable identity fails instead of silently choosing another endpoint. | Configured in app settings. Host restart/reopen required. Verify resolved endpoint and transport health from AudioHost status. No endpoint substitution. |
| Voice Capture endpoint | `MicInputEndpointId`; null means Voice Capture is unavailable. An unresolved selection stops Voice rather than falling back to a default device. | Configured separately from Program output; Voice lifecycle must be off/quiescent for relevant changes. Verify Voice status and endpoint identity. Never substitute devices. |
| Voice channel routing | `Stereo`, `Input1Mono`, or `Input2Mono`. Current mono routing copies the selected Windows stream channel to both canonical stereo sides; it does not identify a physical mixer socket. No arbitrary N-channel routing is exposed. | Persisted setting; Voice restart required. Verify selected route/configuration and input-level evidence. Physical connector mapping requires independent QA. |
| Processing format | KLIDAN processing is canonical 48 kHz Float32 stereo. This is an internal representation, not proof of physical endpoint PCM. | Fixed implementation contract, not a profile knob. Physical backend may convert/renegotiate. Verify actual transport telemetry separately. |
| Shared physical format and period | PortAudio/WASAPI Shared; host requests canonical processing format and lets the backend/device negotiate. No operator period/buffer control is exposed here. | Not a profile-tunable parameter today. Verify actual open format, latency and health telemetry; do not infer from Shared Mode mix format. Shared is a separate fallback and its measurements never qualify an Exclusive profile. |
| Coordinated Exclusive physical formats | Native transports currently negotiate bounded WASAPI formats, with 48 kHz stereo PCM16 as the principal path; Capture can have a mono candidate. The coordinator requires compatible paired endpoints/format and matching buffer geometry. | Not a free-form operator setting. Exact endpoint and stream initialization are revalidated on open. Initialize-only evidence cannot establish the paired coordinator. No silent conversion to a different format. |
| Period / buffer | Coordinated native clients request the minimum device period, use the existing alignment retry, and report actual buffer frames/period. No user-selectable profile value exists. | Host restart required. Verify actual Master and Voice status/period/buffer after start; requested values are not actual values. Shared backend buffering is not a manual knob. |
| Voice ring | Voice PCM ring has fixed capacity 4096 frames (about 85 ms at 48 kHz). Capacity is not measured queue depth or latency. | Fixed implementation value. Runtime depth/high-water/drop evidence is reported; no profile tuning is supported. Do not treat larger capacity as clock-drift correction. |
| Health / latency | AudioHost reports open/healthy state, actual transport, stream/buffer/period and backend latency where available, plus ring/callback/underrun/discontinuity/coordinator diagnostics. These do not prove acoustic RTL or KLIDAN physical verification. | Runtime observations, not durable settings. Confirm against a bounded real stream and physical QA. A status report is not a transactional profile-application receipt. |

Configuration is through the existing operator settings and device selection. There is no production profile import path, automatic format tuning, or safe profile rollback transaction yet. The legacy NAudio Program path remains authoritative until its separate cutover gate; AudioHost mode support is not a promise that every product flow uses the native AudioHost.

## Probe evidence mapping

| Profile fact | Existing Probe source | Evidence boundary / missing proof |
|---|---|---|
| Exact endpoint ID, direction, state, name, Shared Mode properties | D3-B1 `ListWindowsEndpoints`; `WindowsEndpointDto` | Real Windows metadata only. Shared Mode format is not Exclusive support; driver/firmware and physical mapping may be unknown. |
| Exact Exclusive support, encoding, rate, channels, HRESULT, Default/Minimum device periods, cleanup | D3-B2 `QueryFormatMatrix`; shared `ExclusiveFormatMatrix` and Windows adapter | `S_OK` is `SUPPORTED_REPORTED`; periods identify query-result rows, not independent period support queries. No Initialize/Start. |
| Initialize-only, exact period, alignment retry, buffer/event evidence, release status | D3-B2 `ValidateFormatMatrix`; consent-gated one-candidate operation | No Start, no PCM, no stability. `RELEASE_ATTEMPTED` is not `CLEANUP_CONFIRMED`. |
| Bounded Capture/Render Start, observed PCM, stability, Duplex, routing | Existing CLI has separate bounded stream/duplex/capture/render/loopback/PRBS paths | Not imported by this C1 GUI profile producer. Each requires its own result and safety review; independent Capture + Render does not equal coordinated Duplex. |
| KLIDAN physical QA | Operator-attested evidence only | Not emitted by Probe automatically and not present in the C1 producer input. |

The profile producer accepts only the current selected active real endpoint and its accepted `REAL_WASAPI_QUERY`, plus an optional matching `REAL_WASAPI_INITIALIZE_ONLY` result. It does not enumerate endpoints, call WASAPI, repeat tests, or combine prior endpoint results. Query rows and Initialize evidence remain distinct records with exact period and provenance. All unobserved states are `NOT_RUN` or `UNKNOWN`; no Render/Duplex result is synthesized.

## Profile v1

Machine schema: [`device-configuration-profile-v1.schema.json`](../schemas/device-configuration-profile-v1.schema.json). The Core parser additionally enforces semantic rules, rejects duplicate JSON properties and unknown fields, bounds JSON to 1 MiB/depth 32, and validates format geometry, directions, evidence references, state/HRESULT consistency, and recommendations. Profile validation never calls the worker or touches hardware.

The document has profile/build/platform/backend metadata, OS description and process architecture, nullable device identity, separate exact Capture/Render endpoint bindings, evidence records, capability evidence references, per-mode AudioHost assessments, and a separate recommendation object. A profile with one Capture endpoint is valid and explicitly partial. Local endpoint IDs are retained only in `LOCAL_MACHINE`; `SANITIZED_PORTABLE` removes endpoint IDs and names and marks endpoint bindings unbound. A sanitized export is not locally bound and cannot authorize application.

Current C1 assessments always set `eligibleForRecommendation=false` and keep preferred/fallback null. An Exclusive Initialize result cannot qualify a recommendation. The schema records unknown device/driver/firmware/clock/USB facts as null. It does not persist PortAudio indices, serials, user paths, or machine paths. It distinguishes 48 kHz Float32 stereo processing format from physical PCM.

Local profile storage defaults to `%LOCALAPPDATA%\KlidanAudioProbe\Profiles\profile-{profileId}.json`. Save uses a same-directory temporary file, flush, and no-overwrite atomic move; existing files are never replaced. The Result step exposes **Сохранить профиль для KLIDAN** only while current accepted real evidence still matches the selected active endpoint. The UI reports the full destination and limitations. It performs no new hardware operation.

## Audient regression example

The operator-provided Audient iD4 MKII Capture observation is used only as a labelled test fixture: PCM16, 48 kHz, 2 channels, DEFAULT 10 ms, 480 frames, support `S_OK`, Initialize `S_OK`, event ready, cleanup `RELEASE_ATTEMPTED`, no Start/PCM. The fixture has Capture-only evidence, no Render binding/evidence, no current-build Duplex, and no recommendation. It contains no invented driver, firmware, hardware clock, USB mode, or physical input mapping. Historical 3 ms / 144-frame AudioHost observations are not joined to this fixture.

## Safe future application and rollback lifecycle

This is a future KLIDAN requirement; no production code was changed:

1. Read and strictly validate the local profile; confirm its endpoint binding still resolves to the exact ID, direction, and active state.
2. Compare device/container identity and driver/firmware context; reject stale or ambiguous evidence. Never substitute another endpoint.
3. Require the requested mode/configuration to reference sufficient matching evidence. Exclusive is the intended professional-device target only with a matching qualified profile; Shared remains a distinct home/media fallback. No automatic mode change while Program/Voice is active or in an unknown lifecycle state.
4. Snapshot current settings, stage the candidate without committing it, quiesce affected streams using existing lifecycle gates, restart, and read back exact endpoint, transport, format, period/buffer, and health.
5. Commit only after readback and required QA pass. On failure, stop the candidate and restore the snapshot, then verify rollback. If rollback cannot be confirmed, report UNKNOWN/FAULTED and require operator recovery; never claim clean rollback or start on a substitute device.

AudioHost currently lacks a unified exact applied-configuration receipt and transactional profile apply/rollback API. Probe profile creation does not close that production gap.

## Gaps and ordered next work

**Already supported:** endpoint discovery; exact format query; consent-gated Initialize-only; separate CLI stream/duplex/loopback tests; current settings for Shared/Coordinated Exclusive, exact endpoints, and Voice routing; runtime health/latency telemetry.

**Implemented by C1:** versioned profile schema/model; strict parser/semantic validation; local and sanitized serialization; no-overwrite atomic local save; current real-result projection in the Result step; operator-reported Audient fixture.

**Requires implementation:** profile evidence adapters for accepted Capture/Render/stream/Duplex/physical QA reports; evidence age/invalidation policy; portable identity matching beyond friendly name; AudioHost exact applied-state receipt; profile import/evaluator; transactional settings apply and verified rollback.

**Requires physical QA:** real device stream start/stop and PCM observation; per-direction stability; joint Capture/Render coordinated behavior; Voice route/connector checks; KLIDAN playback/Voice workload; validated cleanup/recovery. Initialize-only or an Exclusive query cannot stand in for these.

**Future research:** cross-machine identity matching without private IDs; driver/firmware/USB mode acquisition; profile expiry thresholds; whether non-WASAPI transports need separate evidence types. No tunable AudioHost parameter is proposed unless production AudioHost exposes it.

Ordered work: (1) review/accept profile schema in both products, (2) add Probe adapters for real stream and Duplex evidence, (3) add AudioHost applied-state receipt, (4) define evidence sufficiency and invalidation, (5) implement isolated staged apply/rollback, (6) run a separately authorized Audient pilot and verify rollback before any recommendation is applied automatically.
