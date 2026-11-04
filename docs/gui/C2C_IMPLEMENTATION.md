# C2c — Duplex integration and configuration evaluator

**Status:** Code path implemented; no physical audio operation was performed for this change. The reported Audient measurements below are operator-provided historical evidence only.

## Native path and report

The worker reuses the existing `KlidanAudioProbe.exe --test-duplex` coordinator. It launches the bundled CLI as a supervised child and validates the child's structured report before returning it over IPC. Duplex stream servicing remains in the existing native Capture/Render workers; C2c adds report capture and the GUI/profile path rather than a second WASAPI engine.

The CLI accepts `--output-json FILE` for Duplex, creates the file without overwriting, and accepts worker-only `--operation-id UUID` and inherited `--cancel-event-handle HANDLE` options. New report contract/schema: [`wasapi-duplex-validation-v2.schema.json`](../schemas/wasapi-duplex-validation-v2.schema.json). The prior v1 schema remains available for saved reports. The IPC envelope/protocol and profile v3 versions are unchanged; profile parsing accepts embedded Duplex report v1 and v2. The production worker's `DuplexValidationReport::Validate` checks the actual native serializer output before accepting it.

Schema v1 records the exact requested Capture/Render endpoint IDs, 48 kHz stereo PCM16, period, duration and MMCSS; each side's HRESULTs, buffer geometry, servicing counters, frame coverage, event interval summary, quality, error stage, timeout/device-loss/cancellation flags, Stop result and release evidence; and Duplex readiness, coordinated Start authorization, Start difference, overlap duration, peer-stop and terminal classification. Not-reached measurements are nullable. Render `silentBuffersServiced` counts successfully released buffers marked `AUDCLNT_BUFFERFLAGS_SILENT`; it is digital submission evidence, not DAC playback. Capture device-position anomalies and unavailable packet buffers remain separate from discontinuities and timestamp errors.

Report v2 separates `serviceFaults` (critical stream/API service failures), `packetDrainLimitHits`, `intermediateWaitTimeouts`, and `longEventIntervals`. A bounded intermediate wait that is followed by resumed event servicing remains visible on both sides but does not alone make the run dirty. Packet-drain-limit hits, watchdog timeouts, long event intervals, insufficient frame coverage, API failures, discontinuities, timing/position anomalies, failed Stop, and incomplete cleanup continue to block `DUPLEX_CLEAN`.

`DUPLEX_CLEAN` is rejected unless both sides have successful exact-format/Initialize/Start/Stop HRESULTs, ready event-driven streams, bounded-duration completion, at least 90% expected frames, no material service or timing faults, observed Render silent buffers, overlap of at least 90% of the requested run, and `RELEASE_ATTEMPTED` cleanup. A release attempt is not proof that COM release succeeded. Process exit code alone never makes a report clean.

## Worker control and cancellation

`ValidateDuplex` requires two distinct endpoints from the latest explicit Windows discovery, correct Capture/Render directions, one-use consent, and immutable 48 kHz / stereo PCM16 / period / duration / MMCSS settings. The worker revalidates the exact active endpoint IDs immediately before launch. No matching by friendly name occurs.

`CancelDuplexValidation` signals the inherited manual-reset event. The existing Duplex coordinator sees the cancellation and requests both stream workers to stop. Cancellation can arrive before the worker dispatches `ValidateDuplex`; one bounded pending operation UUID latch handles that ordering and applies only to a matching next request. The native child is created suspended, assigned to a `KILL_ON_JOB_CLOSE` Job Object, and only then resumed. On timeout the worker requests cooperative cancellation, waits up to three seconds, then terminates the Job Object and waits up to three more seconds. A forced termination, missing report, or lost IPC yields UNKNOWN; it does not claim successful Stop or cleanup.

The worker performs no audio API work on the pipe reader thread. A disconnected worker does not imply a cleanly stopped stream. No automatic restart is performed.

The GUI `ValidateDuplex` response deadline is 51 seconds: the worker accepts at most a 10-second run, its child wait allows requested duration plus 25 seconds, then allows 3 seconds for graceful cancellation and 3 seconds after forced Job Object termination, with a further 10 seconds reserved for process setup and report handling. If that deadline or caller cancellation wins after the request may have been sent, the GUI ends the worker transport and closes its kill-on-close Job Object. The operation and cleanup remain UNKNOWN; any late response is rejected by correlation and cannot populate another request. The existing `ValidateStream` deadline remains 45 seconds.

## GUI pair workflow and consent

On the Windows Devices screen, the operator explicitly selects one active Capture and one active Render endpoint. No pair is inferred from similar names. The selected exact pair and independent endpoint names are shown; changing either endpoint clears current pair results and consent, and requests cancellation if an operation is active.

The Test step offers separate **Проверить MINIMUM** and **Проверить DEFAULT** actions. Each opens a fresh consent for that exact pair and period, 5-second maximum stream duration, and MMCSS OFF. The confirmation states that both endpoints open Exclusive, Capture packets are discarded without saving microphone audio, Render sends digital silence, no Capture-to-Render route or test tone is used, and cancellation coordinates both Stops. Each accepted period remains visible in its own result card, including NOT_RUN periods. The GUI stays on the Test step after either result so the other period can be run next; previous accepted evidence is retained if a later attempt fails. The Result step and its **Сохранить профиль для KLIDAN** action remain available through the operator navigation. No fresh consent is reused between periods.

The Result view reports Worker connection and Windows discovery freshness independently. Source review confirms that normal `RunDuplexCli` completion closes the supervised CLI child and its child Job Object; it does not shut down the GUI-owned worker. The GUI marks discovery stale when refresh begins or the worker enters a disconnected/non-connected state. The former combined message obscured which condition applied. If WPF clears a selected pair while its endpoint source switches to historical data, that deselection no longer erases the accepted report; a successful fresh discovery explicitly clears the old pair before the operator selects again. No automatic reconnect is performed. If the worker actually exits, its accepted evidence remains historical and no new operation can use the stale endpoint pair.

## Evaluator

`DeviceConfigurationEvaluator` is deterministic and binds a candidate to Capture ID, Render ID, 48 kHz, stereo PCM16, WASAPI Exclusive, period, MMCSS, duration and observed buffers. Synthetic provenance, pair/settings mismatch, incomplete coverage, faults, failed Stop, unknown cleanup, absent overlap, or missing evidence cannot produce a clean recommendation.

When both independent short runs are clean, MINIMUM can be `PROBE_PROVISIONAL` preferred and DEFAULT fallback. A nonzero intermediate wait-expiration count by itself does not disqualify a run, but the separately reported material faults remain evaluator gates. If only DEFAULT is clean, it is preferred without an invented fallback. If neither qualifies, the result explains why. Every profile keeps `AudioHostEligibility: NOT_CERTIFIED`; five-second Probe evidence is not long-duration stability, coordinated AudioHost, acoustic synchronization, physical routing, or RTL certification.

## Combined profile v3

Pair observations use Device Configuration Profile v3 and [`device-configuration-profile-v3.schema.json`](../schemas/device-configuration-profile-v3.schema.json). They retain exact local Capture/Render bindings, a separate evidence ID for each observed period, the structured report, candidate recommendation and limitations. Saving uses existing local atomic, no-overwrite profile storage. Sanitized export redacts endpoint IDs in bindings and nested reports and is unbound. The parser continues to read valid v1/v2 single-endpoint profiles; v1/v2 records are not silently combined into a v3 pair.

No independent single-stream or Duplex evidence is fabricated from the other direction. No profile import or AudioHost activation is implemented here.

## Verification

- `probe_duplex_validation_report_tests` exercises v2 JSON emitted by `DuplexValidationReport::ToJson` through the same `DuplexValidationReport::Validate` used by the production worker, for MINIMUM and DEFAULT. It verifies benign intermediate waits pass while service faults, watchdog, long intervals, packet-drain limits, insufficient frames, and unknown cleanup cannot be labelled clean.
- GUI Core tests cover fault-aware recommendations, pair/profile binding, v1/v2 nested report compatibility, sequential periods without forced navigation, retained accepted evidence after a later timeout, and separate worker/discovery state presentation.
- Worker IPC/process tests use an injected fake backend and local named pipes only. They cover exact endpoint identity, both periods, benign wait and material-fault report scenarios, consent replay/mismatch, cancellation, worker report parsing, and incomplete coverage.
- Native CTest covers the native report contract and existing CLI regressions. No test opens a physical audio endpoint.

## Historical operator-provided Audient evidence

These results predate this implementation and are not loaded as current scan evidence or inserted into generated profiles:

| Period | Capture | Render | Duplex | Start difference | Simultaneous runtime | Capture / Render coverage |
|---|---|---|---|---:|---:|---:|
| MINIMUM, 144 frames / 3 ms | CLEAN | CLEAN | CLEAN | 0.026 ms | 5001.246 ms | 99.94% / 100.00% |
| DEFAULT, 480 frames / 10 ms | CLEAN | CLEAN | CLEAN | 0.030 ms | 5001.168 ms | 99.98% / 100.18% |

Both were reported at 48 kHz stereo PCM16, WASAPI Exclusive, MMCSS off, with no discontinuities or critical errors. These C2c.1 historical runs establish neither ADC/DAC physical synchronization nor drift from frame-count differences. Long-duration stability, acoustic RTL and production AudioHost QA remain unmeasured.

The operator also reported a later MINIMUM run with successful Capture/Render Initialize, Start, and Stop, 99.96% / 100.02% coverage, no listed discontinuities/timestamp/device-position anomalies, but one `serviceFaults` count per side and overall `DUPLEX_WARNINGS`. That report used v1, where `serviceFaults` combined critical errors, packet-drain-limit hits and intermediate wait expirations, so the individual cause cannot be reconstructed from that saved result. It is not retroactively reclassified as clean. Generate a new v2 report to see the separate counters. The operator reported that the GUI then moved to Result and showed a combined worker/discovery historical warning; this code fix keeps the Test step and displays those two freshness states independently. No physical retest was performed for this change.

### Operator-only Audient retest checklist

1. In Windows Devices, explicitly refresh and select the intended active Capture endpoint and Render endpoint by their exact device entries. Confirm the pair; the app does not infer it from matching names.
2. Close applications that may hold either endpoint in Exclusive mode. Run MINIMUM and DEFAULT as separate tests, each with fresh consent; the operation opens both endpoints Exclusive, discards Capture packets, and submits Render silence.
3. For each result, review both sides' Initialize/Start/Stop, frames and coverage, faults, overlap, and cleanup. Save the pair profile only after reviewing the exact endpoint IDs and period-specific evidence.
4. Treat worker loss or forced termination as UNKNOWN cleanup. Do not infer acoustic synchronization, clock drift, physical routing, long-duration stability, or production AudioHost approval from these results.

## Remaining limitations

- Live Duplex is limited to 48 kHz stereo PCM16 and the existing DEFAULT/MINIMUM policies; no arbitrary multichannel Duplex claim is made.
- The child process can be terminated within the bounded Job Object shutdown path, but forced termination leaves stream cleanup UNKNOWN.
- Physical Audient retest is operator-only and pending; this task performed no physical WASAPI test.
- No configuration is approved for production AudioHost activation.
