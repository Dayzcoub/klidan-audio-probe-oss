# B4-B2B.2E — Capture-only Hardware QA Preparation

**Target:** Audient iD4 MKII, Windows 10/11 x64

**QA preparation base:** `6aace8f1ef4ac1ffcb93051b9519ff0833745084`
**Procedure revision:** record the full Git SHA of the commit containing this document at each run.

## Scope and current disposition

This document prepares two operator-run, five-second Capture-only checks. It does not authorize either test by itself. An operator must review the setup and explicitly approve each invocation.

- **Case A — Capture stream baseline:** command is ready. It opens only the explicitly selected Capture endpoint. It has no Render endpoint argument.
- **Case B — Capture evidence preflight:** the offline source artifact is available as `klidan-prbs511-v1-48k-mono-5s.wav`; generation and independent verification commands are below. The probe still only captures and analyzes: it never plays this WAV or starts Render.

Neither case measures physical RTL or authorizes output. Expected invariant fields are `OUTPUT_INHIBIT_STATUS: OUTPUT_DISABLED`, `RENDER_STATUS: NOT_STARTED`, and `PHYSICAL_RTL_STATUS: NOT_MEASURED`.

`RENDER_STATUS` reports whether **KLIDAN Audio Probe** started a Render stream. It does not describe the external Realtek player used to feed the Audient input; that player's activity and route must be verified separately.

## Requirements before either case

- Windows 10/11 x64 and the matching Windows audio driver installed and working.
- Build the probe from the reviewed task branch in **Release x64**. Record `git rev-parse HEAD` as `TEST_COMMIT_SHA`; do not substitute a binary from another checkout or an unknown download. The project does not embed a Git revision in the executable.
- Verify the physical device label says **Audient iD4 MKII**. Record the Windows version/build and Audient driver/provider/version from Windows Settings and Device Manager. Do not infer model or driver version from endpoint text alone.
- Identify the exact **Capture** endpoint. An operator may inspect Windows sound settings or run the read-only `KlidanAudioProbe.exe --list` locally. That command was not run during this preparation. Keep the full endpoint ID private; never put it in a shared report.
- Close DAWs, browsers, conferencing applications and other programs that could hold the endpoint. Exclusive Capture may temporarily seize the input.
- Do not connect any output to Audient Input 2. In particular, **Audient Line Out → Instrument Input 2 is not approved**. No Render endpoint is selected in these cases.
- Disable speakers/acoustic outputs, direct/hardware monitoring and any unintended output route. Disable 48 V phantom power. Keep Input 2 disconnected and quiet. Do not change settings automatically; stop if the physical controls or routing are unclear.
- For Case A, no test source is needed. Do not speak into or record a microphone for the test. The program processes Capture buffers for diagnostics only and does not save PCM audio.
- Before each invocation, confirm the chosen endpoint, duration and physical setup, then explicitly approve that single run. There is no unattended loop or auto-repeat.

### Operator-reported Audient iD4 MKII hardware QA

All results in this section were reported by the operator. They were not performed or independently verified in this repository task. Raw logs were not supplied. No driver version, binary hash, exact executable revision, date, or exit code was supplied; none is inferred.

**Format:** WASAPI Exclusive, 48,000 Hz, stereo PCM16.

**Case A — baseline Capture:**

| Period / MMCSS | Frames | Reported result |
|---|---:|---|
| Default / off | 240000 | `CLEAN`; zero continuity faults |
| Minimum / off | 239904 | `CLEAN`; zero continuity faults |
| Minimum / Pro Audio | 239904 | `CLEAN`; zero continuity faults; MMCSS register/revert succeeded |

**Case B — physical PRBS Capture preflight:**

| Run | Period / buffer / MMCSS | Frames / packets | Markers / confidence | Input 2 peak | Reported result |
|---|---|---:|---:|---:|---|
| 1 | Default / 480 frames / off | 239520 / 499 | 13 / 0.689 | −90.31 dBFS | `PREFLIGHT_STATUS: ARMED`; `CAPTURE_EVIDENCE_STATUS: CLEAN` |
| 2 | Minimum / 144 frames / Pro Audio | 239904 / 1666 | 11 / 0.706 | −90.31 dBFS | `PREFLIGHT_STATUS: ARMED`; `CAPTURE_EVIDENCE_STATUS: CLEAN` |

Both Case B reports state Initialize/Start/Stop succeeded; no discontinuities, timestamp or device-position errors, or clipped samples; Capture cleanup succeeded; no Probe Render activation; zero Ring Push calls; and no physical RTL measurement. The reference WAV hash reported was `42d1c3a88048837923e8bb1e7c20b3f80dce0693f3a0ef71e384e6b0c33340ac`.

These are operator-reported observations, not a formal QA attestation produced by the executable. Runtime `CAPTURE_EVIDENCE_STATUS` describes observed Capture integrity only.

## Select the endpoint and keep evidence local

Run from the repository root after building with the README commands (`cmake -S . -B build -A x64`, then `cmake --build build --config Release`). The commands below prompt for the endpoint ID instead of embedding it in shell history. `--test-stream` prints the endpoint ID; its console log must remain private. Do not commit the generated directory or send a raw log.

```powershell
$Exe = (Resolve-Path '.\build\Release\KlidanAudioProbe.exe').Path
$CaptureId = Read-Host 'Paste the privately verified Audient iD4 MKII Capture endpoint ID'
if ([string]::IsNullOrWhiteSpace($CaptureId)) { throw 'Capture endpoint ID is required.' }
$TestCommit = (git rev-parse HEAD).Trim()
$RunDir = Join-Path $env:LOCALAPPDATA ("KLIDAN-AudioProbe\B4-B2B.2E\{0}-case-a" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $RunDir -Force | Out-Null
"TEST_COMMIT_SHA=$TestCommit" | Set-Content (Join-Path $RunDir 'run-metadata-private.txt')
```

Check `$Exe` resolves to the Release binary and the printed commit is the intended test revision before continuing. Save raw output under `$env:LOCALAPPDATA`; sanitize a separate copy for sharing. Keep endpoint IDs, machine/user paths, serials and unredacted logs private. Collect only textual diagnostics; do not record, export or transmit PCM audio.

## Case A — Capture stream baseline

**Initial configuration:** Exclusive Capture, 48,000 Hz, Stereo PCM16, five seconds, `--period default`, MMCSS off. Do not supply `--render-id`.

After the operator has confirmed the setup for this run, execute:

```powershell
$CliArgs = @(
  '--capture-id', $CaptureId,
  '--test-stream', '--duration', '5',
  '--rate', '48000', '--channels', '2', '--format', 'pcm16',
  '--period', 'default', '--mmcss', 'off'
)
& $Exe @CliArgs 2>&1 | Tee-Object -FilePath (Join-Path $RunDir 'case-a-private-console.txt')
$ExitCode = $LASTEXITCODE
"CASE_A_EXIT_CODE=$ExitCode" | Add-Content (Join-Path $RunDir 'run-metadata-private.txt')
```

### Expected observations

Record `IsFormatSupported`, first/final `Initialize` HRESULTs, any alignment retry and final requested period, `GetBufferSize`, event creation/`SetEventHandle`, Capture service, Start and Stop HRESULTs, actual buffer frames, events, packets, frames and coverage, actual runtime, discontinuities, timestamp errors, unavailable Capture buffers, device-position jumps/overlaps, watchdog/cancellation, first/last QPC and device position, endpoint/client/event cleanup, `STATUS`, `STREAM_QUALITY`, and exit code. The normal test-stream path does **not** measure PCM clipping; record that as `NOT_MEASURED`, not zero.

An empty `GetBuffer` result (`AUDCLNT_S_BUFFER_EMPTY`) is normal queue-drain behavior after available packets are consumed; it is counted separately and is not itself a fault. `AUDCLNT_E_BUFFER_ERROR` is a distinct unavailable-buffer fault and contributes to the warning/integrity result. The Case A runtime coverage percentage divides frames by the expected frames for the measured runtime. Case B's Capture evidence coverage uses the fixed nominal five-second denominator of 240000 frames. Record both with their reported context; the percentages do not share a denominator.

### Case A classification

Case A does not emit the Case B-specific `PREFLIGHT_STATUS`, `MARKER_*`, or `CAPTURE_EVIDENCE_STATUS` fields. Their absence in a Case A log is expected and must not be recorded as unknown Case B evidence.

- **PASS — five-second baseline only:** exit code 0; `STATUS: STREAM_COMPLETED`; `STREAM_QUALITY: CLEAN`; Initialize, event setup, Start and Stop succeed (`S_OK`); duration reached; sufficient frame coverage; nonzero packet/frame counts; no discontinuities, timestamp errors, unavailable buffers, position gaps/overlaps, watchdog, cancellation or critical errors; endpoint/client/event released. This does not prove long-term stability, signal quality or RTL.
- **FAIL:** endpoint/format mismatch; Initialize, event setup, Start or Stop failure; API/device invalidation; `STREAM_FAILED`; watchdog; discontinuity, timestamp error, unavailable buffer, position gap/overlap; or insufficient coverage.
- **INCOMPLETE:** operator cancellation, missing diagnostic fields, unexpected warning status or a run whose completion/cleanup cannot be established. Repeat only after reviewing the cause and re-approving a new run.

Exit code 0 is required but is not sufficient without the status and metric checks above. Argument errors return 2; typical device/Initialize, event, Start, stream or warning outcomes are nonzero (for example 4/5/6/7/8). Use the actual printed status and HRESULT as the diagnostic source.

## Case B — Capture evidence preflight

### Offline source generation and verification

`klidan-prbs511-v1` is generated internally by `AnalogLoopback::RenderSignal` for analysis/reference use. The preflight path constructs this expected waveform and analyzes Capture Input 1; it does not emit the signal. CLI parsing only checks that the operator declared the exact string `klidan-prbs511-v1`.

The current reference at 48 kHz is:

- A **511-sample signed PCM16** marker. A 9-bit LFSR starts at `0x1FF`; each output sign is `+1` when state bit 0 is set, otherwise `−1`. Feedback is bit 0 XOR bit 4, then the state shifts right with feedback entering bit 8.
- The expected source is a single-channel (mono) 48 kHz, five-second reference (240,000 frames); route its analog output only to the verified Audient Input 1 path. Do not route a second source channel to Input 2.
- The 511 signs are mean-centered. The first and last 24 samples receive the implementation’s half-cosine edge taper. The resulting marker is normalized and rounded to a maximum absolute PCM16 value of **519** (approximately −36.0 dBFS at the source PCM reference).
- For a five-second, 48 kHz reference, markers start at frame 4,800 (100 ms), repeat every **12,000 frames** (250 ms), and have 511 samples followed by silence. This yields 20 scheduled markers in the reference interval. Marker polarity is independently selected by the implementation’s xorshift32 sequence, seed `0x6D2B79F5`, shifts 13/17/5; the sign is positive when the resulting state’s low bit is set.
- The preflight requires at least five coherent markers on Capture channel 1 and uses the existing correlation/polarity checks. Capture channel 2 must stay at or below the existing **−40 dBFS peak** baseline threshold. Do not replace the sequence with speech, random noise, silence or a generic PRBS of another polynomial/seed.

Build the standalone offline exporter in Windows x64 Release. It uses the existing `AnalogLoopback::RenderSignal` implementation and has no WASAPI/device code. It refuses an existing destination.

```powershell
cmake --build build --config Release --target KlidanPrbsWavExport
$ReferencePath = Join-Path (Get-Location) 'klidan-prbs511-v1-48k-mono-5s.wav'
& (Resolve-Path '.\build\Release\KlidanPrbsWavExport.exe').Path $ReferencePath
if ($LASTEXITCODE -ne 0) { throw "WAV export failed: $LASTEXITCODE" }
$ExpectedSha256 = '42d1c3a88048837923e8bb1e7c20b3f80dce0693f3a0ef71e384e6b0c33340ac'
$ActualSha256 = (Get-FileHash -LiteralPath $ReferencePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($ActualSha256 -ne $ExpectedSha256) { throw "Unexpected reference SHA-256: $ActualSha256" }
"SHA256=$ActualSha256"
```

Record the displayed SHA-256 with the run metadata, and compare it with the independently verified reference hash from the reviewed build. Do not overwrite a prior artifact; choose a new filename or explicitly move the old file aside. Offline structural, sample, marker, polarity, gap, clipping, negative-analyzer, and repeated-export/hash checks run with:

```powershell
ctest --test-dir build -C Release -R '^probe_prbs_wav$' --output-on-failure
```

The test creates two fresh WAVs, parses their RIFF chunks independently, checks every sample and marker against the C++ reference, verifies their SHA-256 values match, and exercises `MonitorRtlPreflight::CaptureData::Analyze()` on the corresponding stereo fixture (Input 2 is zero). A generated WAV is only a digital reference; it does not prove a playback device reproduces its amplitude, timing, sample rate, or routing.

### External playback and Capture synchronization

- Obtain explicit operator approval for this physical playback and Capture run. The artifact is low-level digital PCM, but the external player's output, device volume, amplifiers and wiring can change the analog level. Use a player with exclusive effects/processing disabled, set to 48 kHz mono PCM16 without normalization, replay gain, AGC, resampling or other effects. Do not boost the source. Verify the selected output device and physical line-out routing before playback; confirm the analog level is safe and below clipping using the device's documented controls/metering. Stop if the route or level cannot be verified.
- Keep playback routed only to the independently verified Audient Input 1 path. Leave Input 2 physically disconnected and silent. **Audient Line Out → Instrument Input 2 remains prohibited** pending independent electrical review.
- Cue the exact file at its beginning in an external player and pause it. After the approved Case B Capture command has started, start playback promptly, ideally within the first second, and leave at least 1.12 seconds of the Capture interval after playback starts. This records the WAV from its beginning, including marker 0 and at least five complete markers, while keeping the probe in Capture-only mode. The player must continue through the capture interval; do not seek, loop, or restart.
- A playback that starts before Capture may leave fewer complete markers at the end. The current matcher searches marker origins and requires only five matches: offline testing shows it can still accept a capture that begins one second into the WAV. Thus `MARKER_MATCHED` does not prove the file began at capture frame 0 or establish playback-to-Capture synchronization. The operator start order reduces that ambiguity but is a manual procedure, not a source-start handshake. If exact source offset is required, a separately specified preflight revision should identify marker 0 and enforce a bounded onset, with offline fixtures for delayed starts, partial files, polarity inversion and truncation before any hardware run.

Offline tests show the existing matcher also accepts a globally inverted source (13 matched markers) and a source beginning one second into the file (10 matched markers). It therefore does not establish exact source polarity or source-file start offset. These are known analyzer limits; do not interpret marker match as verification that the external playback chain preserved polarity/timing. The operator start procedure reduces the chance of these misalignments but does not add source-position evidence.

The runtime emits `HARDWARE_QA_STATUS: OPERATOR_REVIEW_REQUIRED` only after a runtime-observed Capture Start succeeds. This reports that a live run occurred and needs operator review; it is not `PASS` and is not formal QA attestation. Mock, simulated, unstarted, and failed-to-start paths report `NOT_RUN`. Formal operator QA conclusions belong in an independently maintained evidence record such as the operator-reported results above. Synthetic evidence cannot produce an attested hardware-QA pass.

These timing statements describe the existing matcher and five-second artifact, not measured physical playback behavior. The operator-reported Capture baselines above are only for Capture stream stability and do not validate playback synchronization or the WAV's analog path.

### Case B command shape — requires a separate approved physical run

After independently verifying the source file and hash, and once the operator separately approves the physical setup, this is the existing Capture-only command shape. It selects no Render endpoint and has no output mode:

```powershell
$CaseBRunDir = Join-Path $env:LOCALAPPDATA ("KLIDAN-AudioProbe\B4-B2B.2E\{0}-case-b" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $CaseBRunDir -Force | Out-Null
"TEST_COMMIT_SHA=$TestCommit" | Set-Content (Join-Path $CaseBRunDir 'run-metadata-private.txt')
$PreflightArgs = @(
  '--capture-id', $CaptureId,
  '--test-monitor-rtl-preflight',
  '--confirm-live-capture', '--confirm-physical-routing',
  '--confirm-test-signal', '--confirm-safe-setup',
  '--test-signal', 'klidan-prbs511-v1',
  '--duration', '5', '--rate', '48000', '--channels', '2', '--format', 'pcm16',
  '--period', 'default', '--mmcss', 'off'
)
# Invoke only after independently verifying the exact source file/hash and obtaining explicit operator approval for playback and Capture.
& $Exe @PreflightArgs 2>&1 | Tee-Object -FilePath (Join-Path $CaseBRunDir 'case-b-private-console.txt')
$ExitCode = $LASTEXITCODE
"CASE_B_EXIT_CODE=$ExitCode" | Add-Content (Join-Path $CaseBRunDir 'run-metadata-private.txt')
```

The four `--confirm-*` switches are explicit operator declarations, not proof of a verified source or safe physical switches. They do not constitute formal QA attestation.

### Case B runtime classification

- **Runtime preflight pass only:** exit code 0 and `PREFLIGHT_STATUS: ARMED`; Input 1 marker `MATCHED` with at least five coherent markers; Input 2 baseline `QUIET` at or below −40 dBFS; at least 237,600/240,000 frames; no clipping, discontinuity, timestamp error, device-position gap/overlap, buffer-unavailable event, watchdog, cancellation or API error; `CAPTURE_EVIDENCE_STATUS: CLEAN`; cleanup successful; `OUTPUT_INHIBIT_STATUS: OUTPUT_DISABLED`; `RENDER_STATUS: NOT_STARTED`; `RENDER_START_STATUS: NOT_CALLED`; `PHYSICAL_RTL_STATUS: NOT_MEASURED`. This is runtime diagnostic evidence only, not formal hardware-QA `PASS`.
- **FAIL:** source marker missing/ambiguous/wrong channel or sequence, Input 2 above threshold, clipping, insufficient coverage, continuity/timestamp/API/cleanup failure, cancellation after a started run or nonzero exit.
- **INCOMPLETE:** any required evidence is UNKNOWN/INCOMPLETE, the run is cancelled before complete evidence, or cleanup/coverage cannot be confirmed. Never convert missing marker evidence into PASS.

`PREFLIGHT_STATUS: ARMED` means only that this bounded Capture preflight met its diagnostic checks. It is not `RuntimeSafetyGate::OUTPUT_ARMED`, does not start/authorize Render, and cannot establish physical RTL.

For Case B, exit code 0 is reserved for `ARMED`. CLI validation errors return 2 before endpoint access; endpoint/Initialize or event-preparation failures return nonzero (commonly 4/5); a completed Capture run that does not arm returns 8. Any nonzero code is FAIL/INCOMPLETE according to the fields above, never a pass.

## Repetition and stop conditions

1. Run Case A once with Default/off. Inspect all status fields and release/cleanup before another run.
2. For repeatability, run at most two additional separately approved repetitions with the same settings. Do not loop or auto-repeat. Keep each run in a separate timestamped directory and compare the same metrics.
3. Any later Minimum-period or MMCSS-on comparison is a separate experiment: change one factor at a time, record it, and obtain operator approval again. This document’s baseline is Default/off.
4. Case B source preparation is digitally unblocked. Perform one separately approved physical run only after checking the source hash, output routing/level, Input 1 wiring and Input 2 silence; inspect marker and Input 2 results before considering another.
5. Stop immediately for wrong endpoint, unexpected Render/output activity, unsafe or unclear physical routing, phantom power on, clipping, unexpected Input 2 signal, API/device invalidation, discontinuity/timestamp/position fault, watchdog, failed Start/Stop, insufficient coverage, or operator uncertainty. Use Ctrl+C to cancel a running test; record it as cancelled/incomplete and verify the device was released before any retry.

## Evidence record template

Create one sanitized record per run. Keep full endpoint ID and raw console output only in the local private directory; replace the ID with `[REDACTED]` in any shared copy. Do not attach audio samples.

```text
QA procedure: B4-B2B.2E
Test case / repetition:
Test commit SHA:
Date/time and operator approval:
Device model (physical label): Audient iD4 MKII
Windows edition/version/build:
Driver provider/version/date:
Capture endpoint name (sanitize if needed):
Capture endpoint ID (private only; public copy: [REDACTED]):
Format: 48,000 Hz / Stereo / PCM16
Period mode / MMCSS:
Requested final period / actual buffer frames:
IsFormatSupported / Initialize first / Initialize final / alignment retry:
Event create / SetEventHandle / Capture service HRESULT:
Capture Start / Stop HRESULT:
Events / packets / frames / coverage / runtime:
Discontinuities / timestamp errors / unavailable buffers:
Device-position origin / gaps / overlaps; QPC range:
Watchdog / cancellation / critical errors:
Cleanup (Capture service, client, event, endpoint):
Case B source hash/specification (if applicable):
Input 1 marker / confidence / matched count:
Input 2 baseline peak / status:
PREFLIGHT_STATUS:
CAPTURE_ADAPTER_STATUS / CAPTURE_INITIALIZE_STATUS / CAPTURE_START_STATUS:
CAPTURE_STREAM_STATUS / CAPTURE_INTEGRITY_STATUS / CAPTURE_EVIDENCE_STATUS / CAPTURE_CLEANUP_STATUS:
OUTPUT_INHIBIT_STATUS: OUTPUT_DISABLED
RENDER_STATUS: NOT_STARTED
RENDER_START_STATUS: NOT_CALLED
Exit code / operator PASS-FAIL-INCOMPLETE:
Notes (no raw PCM):
```
