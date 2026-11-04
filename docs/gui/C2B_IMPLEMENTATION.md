# C2b — Real WASAPI Stream Validation

## Implemented boundary

The existing native `KlidanAudioProbe.exe --test-stream` lifecycle is reused. The worker starts the bundled executable by absolute sibling path and consumes its versioned `wasapi-stream-validation` JSON report; it does not trust or parse CLI text. This keeps WASAPI servicing, frame accounting, quality classification, and cleanup in the existing implementation. The GUI bundles both `KlidanProbeWorker.exe` and `KlidanAudioProbe.exe` beside the app.

The worker accepts one exact Windows endpoint ID from the current explicit endpoint-discovery snapshot. It revalidates ID, direction, and ACTIVE state immediately before launching the CLI. Supported operation settings are 48 kHz, stereo PCM16, Capture or Render, DEFAULT or MINIMUM, MMCSS OFF or Pro Audio, and 1–10 seconds. The GUI currently offers a five-second run with MMCSS OFF. No endpoint is selected by friendly name or replaced with a default endpoint.

Capture uses the existing stream service and discards the received sample contents after packet/frame servicing. Render submits WASAPI silence only. There is no PCM file, feedback route, generated tone, acoustic claim, or physical channel mapping evidence.

## GUI operation and consent

Run the Device → Scan workflow and accept its separate query/Initialize consent. After an exact candidate initializes successfully, select its Initialize row and use **Проверить поток выбранного периода · 5 секунд**. DEFAULT and MINIMUM are separate candidates and each requires a new confirmation. A successful Initialize does not start a stream automatically.

The fresh confirmation shows the friendly name and exact Windows endpoint ID, direction, format, period, five-second limit, temporary Exclusive reservation, Capture packet handling or Render silence submission, and that no PCM is saved. If settings or endpoint change, the confirmation is discarded.

While running, **Остановить** sends `CancelStreamValidation` and waits for the CLI to observe its inherited cancellation event, stop the client, and return the terminal structured report. If the worker or pipe disappears, the GUI reports the outcome as unknown. The worker’s Job Object is owned by the GUI; the CLI subprocess is launched under that worker, so GUI/job termination also terminates the child. A timeout that cannot confirm child exit is reported as an unknown outcome.

## Evidence and status

The native JSON report contains schema version, report ID/time, probe version, REAL_WASAPI_STREAM_VALIDATION provenance, direction, exact 48 kHz PCM16 stereo format, period, duration, activation/query/Initialize/Start/Stop HRESULTs, buffer frames and event readiness, events/packets/frames, runtime-relative expected frames and coverage, elapsed duration, discontinuities, timestamp errors, unavailable Capture buffers, buffer-service faults, event wait timeouts, cancellation/watchdog/device-loss flags, stream-quality classification, cleanup, and `physicalPlayback=NOT_MEASURED` / `physicalRouting=UNKNOWN`.

`CLEAN` is accepted only with successful Start, duration reached, successful Stop, positive buffer geometry, sufficient processed frames using the native ProbeMath tolerance, and zero discontinuities, timestamp errors, unavailable Capture buffers, and buffer-service faults. `STREAM_STARTED` alone is not a successful validation. A five-second run is not long-duration stability certification.

Profile schema v2 adds required `streamObservations`, each linked to an accepted Initialize evidence ID. Normalized cleanup status and the raw ordered cleanup stages are both retained. The v1 schema and valid C1/C2 profiles remain supported; profiles without stream observations continue using schemaVersion 1. The v2 contract is [device-configuration-profile-v2.schema.json](../schemas/device-configuration-profile-v2.schema.json). Stream observations do not create Duplex evidence, physical QA, acoustic RTL, production AudioHost eligibility, or an Exclusive recommendation. Sanitized exports retain the stream evidence but remove machine-local endpoint bindings under the existing privacy rules.

## Tests

Automated worker-process tests use an injected fake stream backend and never activate MMDevice/WASAPI. They cover Capture and Render result identity, exact operation correlation, consent fingerprint checks, Start failure, zero frames, insufficient coverage, cancellation over the same pipe reader, and output-model physical-status limits. Core/WPF tests cover consent cancellation/acceptance, endpoint/period binding, result presentation, and rejecting synthetic stream provenance from a real profile. Profile tests cover v1 backward compatibility and v2 local JSON round-trip.

## Operator QA — Audient iD4 MKII

Codex did not run physical audio QA. An operator can validate the existing Audient Capture endpoint as follows:

1. Launch the Release GUI and explicitly connect the worker; explicitly discover Windows endpoints.
2. Select the exact active Audient Capture endpoint. Run Scan and approve its Initialize-only operations.
3. After successful 48 kHz stereo PCM16 Initialize evidence, select MINIMUM and run one separately consented five-second stream test. Review Start, processed frames/coverage, faults, Stop, and cleanup.
4. Repeat independently for DEFAULT with a fresh confirmation. Do not infer that the smaller MINIMUM buffer is preferable.
5. If testing Render, select the exact Render endpoint and use the separate Render candidate/consent. Render sends digital silence; it does not produce an audible test tone.
6. Save the device profile after reviewing the aggregated evidence. It is local evidence, not a production AudioHost authorization.

Do not run this operator procedure without explicit operator approval. Never join Capture and Render or connect microphone input to output. Do not treat the short run as stability QA; no Duplex or physical RTL was measured.

## Limitations

- Real stream validation is limited to the CLI’s existing 48 kHz stereo PCM16 mode; no multichannel live I/O was added.
- Scan remains bounded and candidate-driven. Stream runs are an explicit follow-up action per initialized period rather than an unconsented automatic Start.
- A driver/COM call that stalls cannot always be interrupted safely. Supervisor termination prevents indefinite GUI waiting, but a lost report leaves operation and cleanup UNKNOWN.
- No physical audio, long-duration stability, Duplex, acoustic, or AudioHost QA was run.
