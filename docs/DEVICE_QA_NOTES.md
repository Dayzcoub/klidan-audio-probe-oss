# Equipment evidence and unresolved questions (2026-10-29)

The legacy notes below are historical **operator observations**, not a compatibility database or measurements from this Audio Probe repo. Sections explicitly labeled Audio Probe contain supplied operator reports; they are not tests performed by CI or by this implementation task.

## Audient iD4
- KLIDAN coordinated WASAPI Exclusive physical QA performed on Windows using Audient driver **5.5.1.18152** after setting device hardware clock to **48 kHz**. The previous 44.1 kHz setting led to exact-48-kHz Exclusive format rejections.
- Observed capture/render **144/144 frames (~3 ms)**, working Voice + DSP/VST3 and acceptable subjective monitoring.
- A documented ~24.4-minute session showed pair frame delta 0, no Voice drops/underruns/discontinuities, ring measured depth 144 and high-water 288 frames.
- **No calibrated acoustic round-trip latency** was measured. Exact transport and current device driver must be rechecked before making a reusable profile.
- Original scoped evidence is retained in private laboratory records; no upstream private URL is published here.

## Behringer FLOW 8
- Operator confirmed input strips **1–6 mono**, **7/8 stereo**, and that selecting 7/8 produced sound in KLIDAN Voice.
- Separate operator test: Shared worked with two stereo outputs plus microphone. Coordinated Exclusive with Mic 1 previously failed because none of the tested **48 kHz stereo** physical capture candidates were accepted.
- PR #181 adds **mono capture candidate fallback**, leaving canonical KLIDAN Voice ring stereo; this change still needs Windows build and physical mono Exclusive testing. Not accepted as hardware PASS merely because device exposes mono.
- Confirm exact Windows endpoints rather than confusing hardware channel strips with independently selectable Windows Capture endpoints; check USB Recording/Streaming mode, driver version and firmware.
- Next QA: enumerate all endpoints; test exact mono 48 kHz candidates, Initialize/Start and actual frames for Mic 1–6; verify capture/render actual period sizes and device container; run bounded duplex and report result.
- An upstream implementation discussion exists in private project records; no private URL is published here.

## DEXP U700
- Previously used for KLIDAN Voice capture tests; some long-session artifacts/discontinuities were investigated. No device-specific strategy should be hard-coded without repeatable Probe evidence.

## Audio Probe B2 operator observations — 2026-10-29

All runs used WASAPI Exclusive, 48 kHz Stereo PCM16 and a five-second requested duration. Endpoint IDs are deliberately omitted. These are operator-reported stream observations, not new runs from CI.

| Device / direction | Period / actual buffer | Events and frames | Observation |
|---|---:|---:|---|
| Audient iD4 Capture | Minimum 3 ms / 144 frames | 1,666 events / 239,904 frames | 0 errors; `STREAM_COMPLETED` |
| Audient iD4 Render | Minimum 3 ms / 144 frames | 1,667 events / 240,192 frames including initial prefill | 0 errors; `STREAM_COMPLETED` |
| DEXP U700 Capture | Minimum 3 ms / 144 frames | 833 events / 120,096 frames | Discontinuity at ~2.519 s; `STREAM_FAILED` |
| DEXP U700 Capture | Default 10 ms / 480 frames | 498 events / 239,040 frames | 0 discontinuities; `STREAM_COMPLETED` |
| Bloody Capture | Minimum 3 ms / 144 frames | 3 events / 720 frames | Discontinuity at ~0.046 s; `STREAM_FAILED` |
| Bloody Capture | Default 10 ms / 480 frames | 498 events / 239,040 frames | 0 discontinuities; `STREAM_COMPLETED` |
| Bloody Render | Minimum 3 ms / 144 frames | 500 events / 72,000 frames; no prefill included | ~5.015 s; formally `STREAM_COMPLETED`, but substantially underfed |
| Bloody Render | Default 10 ms / 480 frames | 500 events / 240,000 frames; no prefill included | ~5.014 s; `STREAM_COMPLETED` |

The reported B2 statuses predate the B2.1 frame-coverage gate. Under the new gate, the Bloody Minimum Render frame count is expected to fail for insufficient coverage; it must be repeated with B2.1 before recording a new status. B2.1 allows a frame deficit only within `max(10% of expected frames, 2 × actual buffer frames)` and classifies event intervals longer than 1.5× the buffer-derived interval as a warning. Discontinuities within the first second are labeled startup; later ones are steady-run events. Capture drains at most 64 packets per event. Frame counts are API accounting, not a physical DAC-rate or acoustic-latency measurement. No long-duration stability or RTL is established.

## B2.1 repeat-run metrics — 2026-10-29

Operator-reported B2.1 measurements, kept separate from the earlier B2 observations above:

- Audient iD4 Capture Minimum: 99.34% frame coverage, two event intervals above threshold (maximum 29.873 ms), and one startup discontinuity at 0.678 s.
- DEXP U700 Capture Minimum repeat: 99.82% coverage, P99 event interval 3.078 ms, no reported anomalies. An earlier Minimum run did report a discontinuity at ~2.519 s and only 120,096 frames; the repeat was clean.
- Bloody Capture Minimum: 99.66% coverage, 653 discontinuities, including 519 after the first second; event interval median 0.057 ms and P95 9.938 ms.
- Bloody Render Minimum: 30.00% frame coverage, average event interval 10.007 ms against 3 ms expected; B2.1 result `STREAM_FAILED`.
- Bloody Render Default: 99.99% coverage, average event interval 10.007 ms; `STREAM_COMPLETED`.
- All five Default Period runs (Audient Capture/Render, DEXP Capture, Bloody Capture/Render) ended `STREAM_COMPLETED`.

These remain operator measurements; no device-specific behavior is inferred. Device/QPC positions are diagnostic counters and timestamps, not acoustic RTL or proof that each discontinuity means lost frames. Endpoint IDs are omitted.

## B2.3 operator observations — 2026-10-29

Seven sequential WASAPI Exclusive stream runs on Windows, all 48 kHz Stereo PCM16 with MMCSS `Pro Audio` and 30-second requested duration. Capture and Render were tested one after another; these results do **not** test simultaneous Duplex operation. Endpoint IDs are omitted.

| Device / direction | Period | Result | Coverage | Diagnostics |
|---|---:|---|---:|---|
| Audient iD4 Capture | Minimum 3 ms | `STREAM_COMPLETED`, `CLEAN` | 99.99% | 0 long intervals, 0 discontinuities |
| Audient iD4 Render | Minimum 3 ms | `STREAM_COMPLETED`, `CLEAN` | 100.00% | — |
| DEXP U700 Capture, run 1 | Minimum 3 ms | `STREAM_COMPLETED`, `CLEAN` | 99.97% | 0 long intervals, 0 discontinuities |
| DEXP U700 Capture, run 2 | Minimum 3 ms | `STREAM_COMPLETED`, `CLEAN` | 99.97% | 0 long intervals, 0 discontinuities |
| DEXP U700 Capture, run 3 | Minimum 3 ms | `STREAM_COMPLETED`, `CLEAN` | 99.97% | 0 long intervals, 0 discontinuities |
| Bloody Capture | Default 10 ms | `STREAM_COMPLETED_WITH_WARNINGS`, `ANOMALIES_PRESENT` | 99.93% | 1 long interval, 0 discontinuities |
| Bloody Render | Default 10 ms | `STREAM_COMPLETED`, `CLEAN` | 100.00% | — |

Totals: seven completed runs, six `CLEAN`, one with warnings, zero `STREAM_FAILED`. These are supplied operator reports for sequential single-direction tests; they do not establish Duplex behavior, clock synchronization/drift, long-term stability, physical DAC rate or measured RTL.

## B3-A operator Duplex results — 2026-10-29

Audient iD4 Capture and Render ran simultaneously on Windows in WASAPI Exclusive, 48 kHz Stereo PCM16, Minimum Period 3 ms, MMCSS Pro Audio. Both the 5-second and 30-second tests completed successfully. The following detailed metrics are from the 30-second run; endpoint IDs are omitted.

| Metric | Capture | Render |
|---|---:|---:|
| Events | 10,000 | 10,001 |
| Frames after Start | 1,440,000 | 1,440,144 |
| Coverage | 100.00% | 100.01% |
| Mean / P99 / maximum interval | 3.000 / 3.065 / 4.010 ms | 3.000 / 3.063 / 4.007 ms |
| Long intervals | 0 | 0 |
| Critical errors | 0 | 0 |
| Status / quality | `STREAM_COMPLETED` / `CLEAN` | `STREAM_COMPLETED` / `CLEAN` |

The Start completion difference was 0.001 ms; simultaneous running time was 30,000.467 ms; processed-frame count difference was 144 frames. Capture had zero discontinuities, position jumps or overlaps. MMCSS registration/revert and both Start/Stop calls returned `S_OK`; overall `DUPLEX_STATUS` was `STREAM_COMPLETED`, overall quality `CLEAN`, exit code 0. This confirms a successful bounded simultaneous stream run only. Hardware clock synchronization, long-term clock drift and RTL were not measured.

## B3-B Clock Probe operator results — 2026-10-29

Four Audient iD4 tests used WASAPI Exclusive Duplex, 48 kHz Stereo PCM16, Minimum Period 3 ms, MMCSS Pro Audio, and Clock Probe. One run lasted 5 seconds and three lasted 30 seconds. Endpoint IDs and local logs are omitted. All four runs were `DUPLEX_STATUS: STREAM_COMPLETED`, `DUPLEX_STREAM_QUALITY: CLEAN`, Clock diagnostic `AVAILABLE` / data quality `CLEAN`, with exit code 0. All returned `GetPosition` snapshots were valid; there were no `S_FALSE` results or API errors.

| 30-second run | Capture rate | Render rate | Relative rate difference | Estimated phase change |
|---|---:|---:|---:|---:|
| 1 | -0.2 ppm | -0.2 ppm | 0.0 ppm | +0.001 ms |
| 2 | -1.2 ppm | -1.1 ppm | -0.2 ppm | -0.005 ms |
| 3 | -0.5 ppm | -0.4 ppm | -0.1 ppm | -0.003 ms |

Each displayed R² was `1.00000` (rounded); individual-series RMS residuals ranged from 0.013 to 0.022 ms. The 5-second test was also `CLEAN` / `AVAILABLE`, with a reported relative difference of -1.0 ppm.

These operator runs functionally and physically validate B3-B clock diagnostics on this Audient setup. No substantial relative rate difference was observed over the tested intervals. The rounded outputs do not establish 0.1 ppm accuracy, absolute hardware synchronization, zero physical clock drift, acoustic latency/RTL, or multi-hour stability under real DSP load.

## B4-A Audient iD4 live-monitor operator result — 2026-10-29

WASAPI Exclusive, 48 kHz Stereo PCM16, Minimum Period 3 ms, MMCSS Pro Audio, 30-second run. Endpoint IDs and local logs are omitted.

- `DUPLEX_STATUS: STREAM_COMPLETED`; `DUPLEX_STREAM_QUALITY: CLEAN`; `MONITOR_STATUS: MONITOR_COMPLETED`; exit code 0.
- Capture received 1,440,000 frames; Render consumed 1,439,712 frames; 288 frames remained in the ring.
- Underrun / overflow: 0 / 0. Startup silence: 432 frames.
- Operator heard signal in one channel, consistent with the current independent input 1/2 to L/R routing. The run did not include PCM level metering or report dBFS values.

This records a successful bounded transport run and operator-reported audibility. It does not infer signal amplitude from this run, prove audibility from software status, measure RTL, or establish long-term stability. B4-A.1 level results are recorded separately below.

## B4-A.1 Audient iD4 signal-level operator result — 2026-10-29

WASAPI Exclusive, 48 kHz Stereo PCM16, Minimum Period 3 ms, MMCSS Pro Audio. Endpoint IDs and local logs are omitted.

| Diagnostic | Left | Right |
|---|---:|---:|
| Capture peak | 0.00 dBFS | -90.31 dBFS |
| Capture RMS | -19.65 dBFS | -92.02 dBFS |
| Render peak | -24.00 dBFS | Digital silence |
| Render RMS | -43.65 dBFS | Digital silence |

Run statuses were `DUPLEX_STATUS: STREAM_COMPLETED`, `MONITOR_STATUS: MONITOR_COMPLETED` and `SIGNAL_STATUS: SIGNAL_PRESENT`. Underrun/overflow were 0/0. The operator confirmed the signal was audible on the left and will reduce the excessive input gain. B4-A.1 is accepted for this tested hardware configuration. These measurements distinguish transport completion, digital signal levels and operator-reported audibility; they do not generalize to other gain/channel configurations, measure RTL or establish long-term stability.

## B4-B1 implementation status

B4-B1 adds a separately confirmed physical analog Render→DAC→cable→ADC→Capture test. The Audient cable setup is **Line Out L → TRS cable → Line Input 1**; disconnect microphones, 48 V phantom power, speakers and direct input monitoring first. Never feed the line output into an XLR mic input. The software Loopback Mixer is not a substitute for this physical route. B4-B1 results must remain distinct from B4-A Capture→Ring→Render live-monitor latency and from speaker acoustics or driver-only latency.

### B4-B1 operator observations — 2026-10-29

Audient iD4, WASAPI Exclusive, 48 kHz Stereo PCM16, 3 ms period, MMCSS Pro Audio. Two physical loopback runs completed with `DUPLEX_STATUS: STREAM_COMPLETED` / `CLEAN`, but both reported `LOOPBACK_STATUS: SIGNAL_NOT_FOUND` and zero detected markers. Run 1 Capture Peak/RMS were −80.77/−93.59 dBFS; Run 2 were −32.97/−67.40 dBFS. Run 2 Capture packet fit RMS residual was 46.829 frames. Endpoint IDs and local logs are omitted.

No delay was measured. The measurements do not yet distinguish whether marker generation/delivery, analog path response or correlation sensitivity caused the misses. B4-B1.1 adds independent Render PCM counters and below-threshold correlation diagnostics to make that distinction visible; it does not change the −36 dBFS ceiling or detection threshold.

### B4-B1.2 operator observation — 2026-10-29

Audient iD4, WASAPI Exclusive, 48 kHz PCM16, Minimum 3 ms, MMCSS Pro Audio. Duplex completed `STREAM_COMPLETED` / `CLEAN`. Render reported 20/20 markers submitted, 10,180 nonzero frames and −36.01 dBFS peak. Capture measured −51.42 dBFS peak and −75.25 dBFS RMS. The global absolute normalized correlation maximum was 0.591447, below the unchanged per-marker threshold 0.80. The five strongest candidate offsets were 17,722.795, 41,722.795, 161,722.796, 173,722.795 and 197,722.796 frames; their separations were multiples of 12,000 frames and candidate polarities agreed with the generated marker polarity sequence. The B1.1 build therefore reported `SIGNAL_NOT_FOUND`; this is strong evidence of a real physical signal, but no delay was measured.

B4-B1.2 adds a separate coherent-sequence detector and synthetic positive/adversarial tests. The sequence detector has not yet been run on hardware, so the operator observation above is not a B4-B1.2 acceptance result. Endpoint IDs and local logs are omitted.

### B4-B1.3 timing observations — 2026-10-29

Three further Audient iD4 physical runs used 48 kHz PCM16, WASAPI Exclusive, 3 ms period and MMCSS Pro Audio. All three reported clean Duplex, 20/20 coherent markers and `LOOPBACK_STATUS: MEASURED` under the B4-B1.2 timing gate:

| Run | Median latency | Full marker variation | Capture packet fit RMS residual |
|---|---:|---:|---:|
| 1 | 19.171981 ms | 0.995002 ms | 6.731 frames |
| 2 | 19.217089 ms | 0.064086 ms | 1.024 frames |
| 3 | 19.167179 ms | 0.947528 ms | 12.233 frames |

In Run 3, the first two marker estimates were about 19.87 ms, then changed to 18.93 ms and later rose to 19.36 ms. Capture packet fit rate error was −105.9 ppm. The prior gate relied on regression fit/R² and did not sufficiently constrain per-marker timing variation; Run 1 and Run 3 therefore require reclassification by B4-B1.3. The recorded values and `MEASURED` labels are historical outputs of the prior implementation, not acceptance under the new gate. No endpoint IDs or local logs are included.

B4-B1.3 adds an independent timing-quality result. At 48 kHz one frame is 20.833 μs. `GOOD` requires all fit residuals, local timestamp deviations, full marker-delay range, adjacent delay jumps and fitted delay trend to stay within 6 frames (0.125 ms). Values through 24 frames (0.5 ms) are `WARNING`; larger values, invalid/incomplete mappings, discontinuities, timestamp errors or failed clock/continuity checks are `UNRELIABLE`. The maximum clock-fit residual includes warmup and final IAudioClock samples because Render interpolation may use those points even though the rate regression trims them. Render clock interpolation brackets are bounded separately: ≤500 ms for `GOOD`, ≤1 s for `WARNING`. Only `TIMING_QUALITY: GOOD` can yield `LOOPBACK_STATUS: MEASURED`; when every marker maps, warning/unreliable runs retain the full delay values and summaries without receiving a measured status. Incomplete mapping retains available marker diagnostics and reports the mapping failure. No marker is discarded as an outlier. These are explicit uncertainty budgets in sample-time units, not claims of calibrated absolute accuracy.

### B4-B1.3 follow-up hardware observations — 2026-10-29

Three additional Audient iD4 runs completed Duplex/CLEAN with all 20 markers matched. Under B4-B1.3, all three were classified `TIMING_QUALITY: UNRELIABLE`:

| Run | Median latency | Capture QPC max local deviation | Full delay variation |
|---|---:|---:|---:|
| 1 | 19.178782 ms | 48.656 frames | ~1 ms |
| 2 | 19.174060 ms | 47.032 frames | ~1 ms |
| 3 | 19.161950 ms | 49.620 frames | ~1 ms |

Capture and Render IAudioClock maximum residuals were approximately 0.85–1.43 ms across the three runs. These readings show the timing anomaly is present in multiple timestamp series, but do not identify its source. B4-B1.4 adds post-run top-five residuals, local step/recovery detail and cross-series coincidence diagnostics. No new B4-B1.4 physical run has been performed; endpoint IDs and local logs are omitted.

### B4-B2A first physical run — Audient iD4, 2026-10-29

WASAPI Exclusive PCM16 Stereo, 48 kHz, 3 ms period. Duplex was `STREAM_COMPLETED / CLEAN`, monitor transport was `MONITOR_COMPLETED`, all 20 synthetic provenance markers were delivered, ring underrun/overflow was 0/0, and prefill was 432 frames / 9 ms. Timestamp analysis found Capture packet/Capture clock/Render clock boundary counts of 1/1/2. Nineteen of 20 marker pairs were rejected by timing; the sole accepted pair estimated 12.0225 ms. Overall `MONITOR_LATENCY_STATUS` was `UNRELIABLE`.

After the common detected QPC step, Capture segment 2 had 1,565 anchors, RMS 0.927 frames and maximum 7.329 frames; Render segment 3 had 1,565 anchors, RMS 0.987 and maximum 7.081; Capture IAudioClock segment 2 had RMS 1.037 and maximum 7.613. The then-current whole-segment maximum gate was 6 frames (0.125 ms at 48 kHz), so each segment failed despite low RMS. This is an observation of timestamp residuals, not attribution to Windows, the driver or hardware.

### B4-B2A.1 local timestamp validation — implementation status

The earlier rejection is now reviewed per marker in bounded 16-anchor local windows. A window needs at least 12 valid bracketing anchors, with four on each side; leave-one-out residuals above 6 frames are listed as isolated QPC outliers, and no more than 20% of a local window may be excluded. The remaining local RMS and maximum residual, Capture packet-vs-IAudioClock local agreement, and Render padding-position uncertainty must fit the unchanged 6-frame GOOD budget. Capture `IAudioClock::GetPosition` QPC is also checked against QPC samples immediately around the call, so a common persistent shift of Capture packet and clock timestamps cannot pass merely because those two series agree. Capture and Render continuity regions remain split at detected >0.5 ms QPC steps, with no interpolation across a boundary. Local window overlap is disallowed when counting independent pairs; at least five independent accepted pairs plus clean complete transport, no ring loss, and the existing delay-spread/uncertainty gates are still required for `MEASURED`.

Render `GetPosition` is bracketed by `GetCurrentPadding` calls and QPC samples; the estimated played frame is interpolated at the returned clock QPC. Capture packet QPC is interpreted per Microsoft WASAPI documentation as the endpoint-recorded time for the first frame's device position, already expressed in 100-ns units ([`IAudioCaptureClient::GetBuffer`](https://learn.microsoft.com/en-us/previous-versions/ms678723%28v%3Dvs.85%29)). The Push interval is a later software operation. Reports now print packet QPC, locally mapped marker QPC, Ring Push begin/end and both Push-minus-timestamp differences. A negative difference is not itself proof of a clock/API/device fault.

Synthetic coverage includes clean 5-second timing, common and one-sided 1 ms steps, isolated and multiple 7–8-frame outliers, sustained offset, gradual drift, boundary-adjacent and post-boundary markers, inconsistent Capture/Render anchors, padding uncertainty and false-MEASURED prevention. Existing analog-loopback tests remain part of CTest. No B4-B2A.1 physical run has been performed; the supplied Audient result remains the prior build's hardware evidence, not validation of this revision. Results continue to describe digital Capture scheduling → Ring → Render stream transport only—not physical analog RTL, ADC/DAC latency, or complete microphone-monitor latency.

### B4-B2A.2 hardware observation — Audient iD4, 2026-10-30

Operator reported a clean WASAPI Exclusive run at 48 kHz PCM16 Stereo, 144-frame period, MMCSS Pro Audio: Duplex CLEAN, Monitor COMPLETED, 20/20 provenance markers verified at Render, and zero ring underruns/overflows. All 20 markers were rejected by the then-current Capture packet/IAudioClock frame agreement check; observed position offset was 142.674–143.027 frames. No latency result was accepted. Packet-QPC to Ring Push was approximately +0.004–0.014 ms. Push-minus-synthetic-marker timestamps cycled near 0, −1 and −2 ms because marker samples are generated at packet-relative positions only after packet service. These are operator observations; they do not establish the physical source of the frame-origin offset.

### B4-B2A.2 implementation status

Capture packet device positions and Capture `IAudioClock` positions are independently mapped to their QPC timestamps and compared at common QPC. The signed frame-origin offset, its local variation and rate difference are reported separately; a stable offset is not treated as a timestamp failure or subtracted to claim absolute accuracy. Local residual, QPC bracket and discontinuity checks remain active. Capture packet QPC identifies the endpoint recording the packet's first-frame position, while the synthetic marker is generated later in the servicing worker; its frame-derived QPC is only packet-position-implied, not marker acquisition time.

The measured metric is now explicitly **Ring Push completion → successful Render `ReleaseBuffer` completion**, with per-marker QPC bounds and median/range. `GetCurrentPadding` is not used as a playback clock: Microsoft documents it as typically unnecessary for event-driven Exclusive streams ([`GetCurrentPadding`](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-getcurrentpadding)). ReleaseBuffer is submission, not DAC playback. `MONITOR_LATENCY_STATUS` therefore remains `UNRELIABLE` for absolute device-time latency until frame-origin and actual playback timing are independently established; software transport reporting is separate. The follow-up B4-B2A.2 operator run below is not validation of the B4-B2A.3 first-frame instrumentation.

### B4-B2A.2 follow-up operator run — Audient iD4, 2026-10-30

Operator reported WASAPI Exclusive, 48 kHz PCM16 Stereo, 144-frame / 3 ms period, MMCSS Pro Audio. Duplex was CLEAN and Monitor COMPLETED; 20/20 provenance markers were accepted at Render and ring underrun/overflow was 0/0. Capture packet/IAudioClock frame-origin offset median was 142.899 frames, variation 0.544 frames, rate disagreement −0.08 ppm. The full-marker software handoff (Ring Push completion for the packet containing the marker's first source frame → successful Render ReleaseBuffer completion containing its final frame) had median 18.0080 ms, minimum 17.9731 ms and maximum 21.0251 ms. Render playback time remains unknown; the result is not hardware or physical latency.

### B4-B2A.3 first-frame handoff — implementation status

The bounded RenderTracker now keeps the marker's first source-frame and Render-frame indices and records the QPC bracket of the successful ReleaseBuffer containing that first frame. It separately retains the final-frame Render index and QPC bracket of the ReleaseBuffer that contains the last frame after complete PCM verification. `SOFTWARE_FIRST_FRAME_HANDOFF` and `SOFTWARE_FULL_MARKER_HANDOFF` each report count, median, minimum, maximum and spread; per-marker output includes both frame positions and timing values. Incomplete, damaged or unsubmitted marker tails are not counted as delivered. Missing QPC brackets omit only that metric sample and remain visible through the count. No transport, ring, gain or prefill behavior changed. B4-B2A.3 has not been physically tested; `MONITOR_LATENCY_STATUS` remains `UNRELIABLE` for device playback time.

## QA labeling
Tag every row as **reported**, **passive probe**, **initialized**, **streaming physical**, **long-run stability**, **measured RTL**, or **KLIDAN accepted**. Attach device/driver/mode/time and actual sample format/period, not just a green icon.

### B4-B2A.3 accepted software handoff evidence — Audient iD4, 2026-10-30

Operator reported WASAPI Exclusive 48 kHz PCM16 Stereo, 144-frame period, Duplex CLEAN, Monitor COMPLETED, 20/20 provenance markers and 0/0 Ring underrun/overflow. First-frame software handoff median was 9.0144 ms with 0.0961 ms spread; full-marker handoff median was 18.0238 ms with 3.0971 ms spread. These are software submission/handoff intervals, not physical playback latency or RTL.

### B4-B2B.1 implementation / design review — Audient iD4 MKII target

The new capture-only preflight is implemented but has **not** been run on hardware. Synthetic tests do not count as hardware acceptance. The test mode requires all explicit operator confirmations and `--test-signal klidan-prbs511-v1`, captures only for five seconds, and can report `ARMED` only with a coherent Input 1 sequence, quiet Input 2 baseline, clean timestamps/positions and no clipping. It never opens Render, starts Render, or pushes audio into the ring; `PHYSICAL_RTL_STATUS` remains `NOT_MEASURED`.

Software routing unit tests establish that Input 1 is copied only to the left staging channel and every right output sample is zero, including rejected/error states; they do not exercise hardware routing. Audient documentation identifies MKII Input 1 as rear mic/line and Input 2 as front JFET instrument. Its line-output lineup is +12 dBu = 0 dBFS, while the published MKII specification does not state the instrument input maximum. A direct Out L→Input 2 return is therefore a **fixture safety blocker** until a safe attenuator/level-matching design is selected and reviewed. No output should be enabled based only on the preflight result. Endpoint IDs and serial numbers are omitted.
