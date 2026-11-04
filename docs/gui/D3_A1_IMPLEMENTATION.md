# D3-A1 — Fake worker and typed IPC foundation

**Status:** implemented for fake-only worker startup, handshake, endpoint listing and idle shutdown. No audio-device operation is implemented.

## Implemented boundary

- The WPF Diagnostics screen starts/connects the matching side-by-side `KlidanProbeWorker.exe` only after an explicit operator action. It can query the worker's synthetic endpoint list and request idle shutdown. D2 local fixtures remain available when disconnected and are visibly distinct from remote fake-worker responses.
- The GUI creates an unpredictable, current-user-only Named Pipe with remote clients rejected and first-instance protection. The worker is launched by exact bundled path with `CreateProcessW(CREATE_SUSPENDED)`. The GUI assigns it to a `KILL_ON_JOB_CLOSE` Job Object before resuming it, then verifies the connected pipe PID and process image path. Security/ownership setup failures fail closed. Connection/handshake is bounded to 5 seconds; idle shutdown is bounded to 2 seconds before Job Object termination. No PATH lookup, service, daemon, automatic start, or retry is used.
- Protocol 1.0 / schema 1 implements the 4-byte unsigned little-endian length prefix and UTF-8 JSON payload, max 1 MiB. The worker rejects zero, oversized, truncated, invalid UTF-8, BOM, non-object, malformed, duplicate-key and excessive-depth frames. It uses RapidJSON 1.1.0 strict parsing; the included license is MIT. No audio PCM or human-readable CLI output crosses IPC.
- Handshake negotiates protocol and command/event/report schema versions. Requests use UUIDs; responses correlate the UUID and command. Version mismatch, protocol failures and command failures carry typed stage/code details. Fake `ListEndpoints` returns synthetic Capture and Render objects only. Idle `Shutdown` returns a correlated response and exits.
- Known unimplemented D1 commands return `NOT_IMPLEMENTED`; unknown command names return `UNKNOWN_COMMAND`. `InspectEndpoint`, format matrix commands, Capture/Render sessions, StopSession and ExportReport are not operational.

## Channel topology

DTOs permit independent Capture and Render stream topologies from 1 through 64 channels, including different counts. Each format may carry its own topology. Channels have stable one-based indices within that stream format. Optional logical groups are explicit metadata; no channel pairing is inferred. Speaker-mask metadata is separate and is not treated as a 48/64-channel routing map. Unknown roles and physical paths remain unknown. Groups do not change digital ordering or imply downmix/duplication.

Deterministic fixtures cover independent channels, four stereo pairs, a mixed topology with four mono channels, three explicit stereo pairs and one six-channel group, and asymmetric Capture/Render counts `(2,8)`, `(32,32)`, `(48,48)`, `(64,32)`, `(16,64)`. Boundary selection is checked at channels 1, 2, 8, 9, 16, 17, 32, 33, 48, 49 and 64. UI channel banks contain eight channels; this is a navigation grouping only and does not create a logical audio group.

## Build and test commands

Run from the repository root in Windows PowerShell:

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

The process tests launch only the side-by-side bundled fake worker and test-only fake processes. They do not enumerate or activate audio endpoints. Record exact results in the delivery report; passing tests are not physical hardware evidence.

Validation completed on Windows x64: CMake Release build passed; GUI Release solution build passed with zero warnings/errors; GUI Core passed 145 checks; worker IPC/process suite passed 37 checks; full native CTest passed 105/105. Desktop smoke check launched the GUI, connected to the fake worker, queried the two synthetic endpoints, performed idle shutdown, and closed the GUI. DPI/scale matrix and full accessibility review were not performed.

## Known limits and D3-A2 prerequisites

- The native worker is a separate CMake target and does not link the existing WASAPI CLI target. The current worker does not initialize COM audio interfaces, call MMDevice/WASAPI, or start Capture/Render.
- This slice does not implement active command consent receipts, session IDs/events, audio-owner threads, service extraction, endpoint enumeration, format validation, Capture meter transport, Render generation, report export, installer/package validation, or live session Stop/cleanup.
- Forced process termination proves ownership cleanup of the process only. It does not prove an audio Stop or orderly resource cleanup. A disconnect never means an audio session stopped cleanly.
- Interactive desktop and DPI review may remain pending if unavailable; automated WPF checks do not establish visual accessibility at every scale.

D3-A2 must first establish a native service seam that can be exercised with fake endpoint/API services, define and test a one-use consent receipt bound to immutable settings, and implement one bounded session state machine with typed progress/failure/terminal evidence. Capture and Render topology remain independent at 1..64 channels. Meter IPC must transport immutable snapshots only from a non-audio publisher; Render must generate only on the selected Windows channel and submit zero to all others. Active Stop, cancellation, device loss, cleanup, and disconnect semantics require process tests before any physical QA. No physical routing claim is permitted.

## Superseded status after D3-A2A

The final two status bullets above describe the D3-A1 baseline and are superseded by [D3-A2A implementation](D3_A2A_IMPLEMENTATION.md): fake active Start/Stop commands, synthetic lifecycle events, consent/fingerprint checks, and a fake service seam are now implemented. Real endpoint enumeration, WASAPI/COM access, audio metering, Render PCM generation, report export, and physical validation remain unimplemented.
