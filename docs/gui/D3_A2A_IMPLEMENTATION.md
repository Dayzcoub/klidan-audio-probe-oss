# D3-A2A — Native Fake Session Control Plane

Status: implemented, fake-only. No WASAPI/MMDevice/COM audio operation, raw PCM, hardware access, or physical QA is part of this slice.

## Delivered path

The WPF Diagnostics screen now separates `LOCAL DEMO` (existing D2 ViewModel fixture workflow) from `WORKER FAKE` (the actual GUI-owned Named Pipe process). Worker mode starts only after an operator action, queries synthetic endpoints over IPC, and requires a fresh consent dialog for every Capture or Render fake operation. The UI shows the remote synthetic endpoint, selected format/topology, channel count, period, MMCSS, duration, scenario, immutable accepted settings, worker events, and a Worker-specific Stop control. Existing local D2 fixtures remain independent.

The native `OperationService` seam has only a `FakeOperationService` implementation. It supports Capture and Render, bounded duration completion, Stop, synthetic warning/failure/device-loss, and one active operation per process. No existing WASAPI or CLI implementation is linked or called.

## Session request and guard

The request contains a local synthetic endpoint identity, direction, format ID, sample rate, encoding, format topology ID, channel count, duration, period, MMCSS setting, nullable Render Windows channel, scenario, and consent receipt. The receipt contains `accepted`, a confirmation UUID, and SHA-256 fingerprint. Fingerprint input is UTF-8 for the exact twelve-field invariant-culture string:

```text
EndpointId|Direction|FormatId|SampleRate|Encoding|ChannelCount|TopologyId|DurationSeconds|Period|Mmcss|RenderChannel-or-null|Scenario
```

The native worker recalculates it and consumes confirmation UUIDs once per worker. It validates the exact synthetic endpoint/direction/format/count combination, 48 kHz supported fixture, duration, period/MMCSS enum, and Render channel range before acceptance. Capture requires a null Render channel. Concurrent Start returns `BUSY`; missing, unaccepted, changed, or replayed consent is rejected. The receipt is an application-level consent record, not an OS security token.

Capture and Render topologies remain independent and format-dependent, with channel counts 1–64 in DTO validation. Channels are ordered stream indices. Optional group metadata is explicit; no pairing or physical connector mapping is inferred. Worker fixtures expose asymmetric Capture/Render endpoint counts and 32/48/64-channel format topologies. Core topology fixtures cover independent channels, four stereo pairs, and a mixed 4-mono/3-stereo/one-six-channel layout. Physical routing is always `UNKNOWN`.

## IPC and event behavior

After bootstrap handshake, one asynchronous GUI reader owns all pipe reads. It dispatches responses by correlation UUID and session events by accepted session UUID plus monotonic `eventSequence`. Start response is written before event production begins. The worker serializes response/event frames and uses overlapped pipe I/O so its session event producer can write while the command loop reads. Request rejections, including `UNKNOWN_COMMAND`, are typed correlated responses and do not disconnect the transport. Unknown correlation responses, stale sessions, and duplicate terminal events are reported separately; invalid event ordering fails the connection. Event sequence JSON uses an integer token, not floating point. A timed-out Start remains outcome-unknown until a late typed rejection or acceptance arrives; a rejection carries its command, code, and correlation so the GUI can report a definite rejection, while late acceptance carries immutable settings and the accepted session. A missing response is never treated as success, and another Start stays blocked while its outcome is unknown.

Implemented events: `Preparing`, `Ready`, `Started`, optional `Warning`, `Stopping`, then exactly one `Completed` or `Failed`. All are synthetic and carry lifecycle, stream-quality classification, cleanup evidence, immutable settings, and `physicalMapping: UNKNOWN`. No event contains a fabricated WASAPI HRESULT. `Started` means only that the fake operation entered its synthetic active phase; it is not `IAudioClient::Start` or proof of sound.

`StopSession` acceptance means only that cancellation was accepted. The worker stops its fake timer, emits `Stopping`, performs a short fake cleanup phase, and then emits a terminal event. Repeated Stop is rejected. If the process/pipe disappears before a terminal event, the GUI presents lifecycle and cleanup as `UNKNOWN`; it never synthesizes `STOPPED` or `Completed`. A device-loss scenario emits `Failed` with cleanup `UNKNOWN` and blocks reuse of that worker process. No session is automatically restarted.

## D3-A2A.1 protocol hardening and GUI smoke corrections

The worker validates the entire bootstrap handshake before command dispatch: bootstrap version, request kind/name/UUID, protocol major and supported minor range, and all three offered command/event/report schemas. It rejects missing or incompatible negotiation data with a bounded `HANDSHAKE/INCOMPATIBLE_PROTOCOL` error and echoes only a valid request UUID. After handshake, required envelope versions, kind, UUID, command bounds, null client correlation/session fields, zero request event sequence, and object payload are checked before the fake service is called. Trustworthy invalid requests receive correlated typed errors; untrusted request identity closes the connection with a typed envelope error.

The WPF request dispatcher keeps response and timeout/cancellation transitions atomic. Cancellation before write removes the request; after a possible write it preserves a bounded unresolved correlation. A late Start acceptance exposes its actual session UUID, request correlation, immutable settings and `ACCEPTED` operation state. A late rejection carries its exact error code and clears the unknown Start result. The client retains at most 32 unresolved request records and refuses further requests at capacity. If the worker disconnects, any unresolved session/cleanup stays `UNKNOWN`; there is no automatic reconnect or session restart.

The Diagnostics screen binds the selected format's channel count to a WPF text element. Capture selection shows its stream topology/count and hides the Render channel selector; Render selection shows a channel selector built from the exact format count. Format changes preserve an in-range channel or reset an out-of-range value to channel 1. After disconnect, the last endpoint response is retained under **«Последний ответ отключённого worker»** as historical evidence. Current endpoint selection is cleared, cached entries cannot be selected, and worker Start/Stop commands remain unavailable. Shutdown explains that another worker connection requires a new GUI process.

Automated GUI tests update the actual XAML `TextBlock` binding for 2, 8, 32, 48 and 64 channel formats and check direction visibility, channel clamping, historical endpoint retention and action gating. Interactive DPI/desktop smoke was not performed in this change.

## D1 proposal deviations and limitations

- D1 proposed active audio events with Initialize/Start/Stop HRESULTs, real format geometry, meter snapshots and reports. D3-A2A emits none of those observations; fake `Ready` and `Started` carry synthetic states only.
- D1 proposed asynchronous meter publication at 10–20 Hz. No meter snapshot IPC is implemented here.
- D1 proposed numeric timeout values. Only the existing bounded worker handshake/idle shutdown and the implemented fake operation/shutdown waits are exercised; these are not validated real-audio Stop constants.
- `ExportReport`, endpoint inspection and format-matrix commands remain `NOT_IMPLEMENTED`. No report is generated by worker sessions.
- The installed fake service is deterministic by settings/scenario but uses a short synthetic preparation/cleanup cadence; it does not model audio timing, device clocks, acoustic output, or real cleanup.

## Validation

Run from a Windows x64 Developer PowerShell at the repository root:

```powershell
cmake -S . -B build -A x64
cmake --build build --config Release
dotnet build KlidanAudioProbe.Gui.slnx -c Release
cmake --build build --config Release --target KlidanProbeWorker probe_worker_stall_fixture probe_worker_mismatch_fixture
dotnet run --project tests/gui/KlidanAudioProbe.Gui.Core.Tests/KlidanAudioProbe.Gui.Core.Tests.csproj -c Release
dotnet run --project tests/gui/KlidanAudioProbe.Gui.Worker.Tests/KlidanAudioProbe.Gui.Worker.Tests.csproj -c Release
ctest --test-dir build -C Release --output-on-failure
git diff --check
```

Worker bundling is required: the GUI build fails with a clear error if the configuration-matched worker executable is missing. Tests use fake local processes and synthetic DTOs only. Passing results are not hardware or acoustic evidence.

## D3-A2B prerequisites

Before any real Capture or Render path, separately design and review audio-thread/COM ownership and test its failure cleanup; integrate existing C1 meter snapshots through bounded non-audio publication; design the actual Render signal safety contract and per-channel digital-silence checks; bind the native safety guard to real endpoint/format initialization evidence; prove cancellation/device-loss/process-exit behavior with fake API injection; define real HRESULT/quality/report semantics; and obtain separate physical-test authorization. D3-A2A does not authorize or implement those operations.
