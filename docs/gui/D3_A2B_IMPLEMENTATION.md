# D3-A2B — Multichannel MeterSnapshot IPC

Status: implemented for **synthetic Worker Fake sessions only**. No audio endpoint APIs, WASAPI, PCM, or hardware are used. The Capture values are deterministic model data, not microphone observations. Render values describe intended digital output only; they do not show DAC, connector, or speaker output.

## Snapshot event schema

Snapshots use the existing protocol 1.0 event envelope. `eventSequence` is a positive exact Int64 allocated by the worker for each transmitted event. `snapshotSequence` is a positive per-session Int64; it increases independently of the global event sequence. Channel `index` is the one-based ordered stream index. Capture and Render payloads are distinct:

```json
{
  "protocolVersion": { "major": 1, "minor": 0 },
  "schemaVersion": 1,
  "kind": "event",
  "requestId": null,
  "correlationId": "<start-request-uuid>",
  "sessionId": "<accepted-session-uuid>",
  "eventSequence": 42,
  "commandOrEvent": "MeterSnapshot",
  "payload": {
    "synthetic": true,
    "lifecycle": "STARTED",
    "streamQuality": "UNKNOWN",
    "cleanupEvidence": "NOT_ATTEMPTED",
    "snapshotSequence": 4,
    "completedWindowIndex": 4,
    "windowDurationMs": 100,
    "channelCount": 2,
    "channels": [
      { "index": 1, "windowPeakDbfs": -26.0, "windowRmsDbfs": -32.0,
        "silent": false, "sessionPeakHoldDbfs": -26.0, "clipCount": 0 },
      { "index": 2, "windowPeakDbfs": null, "windowRmsDbfs": null,
        "silent": true, "sessionPeakHoldDbfs": -28.0, "clipCount": 0 }
    ]
  }
}
```

For Render, each channel uses `intendedPeakDbfs`, `intendedRmsDbfs`, and `digitalSilent`; the payload also requires `selectedChannelIndex` and `intendedOutputModel: "SYNTHETIC_INTENDED_OUTPUT_NOT_MEASURED"`. When a completed window is present, only the selected stream index has intended levels and every other channel is digitally silent in the model. With `completedWindowIndex: null`, every channel measurement field is null, including `digitalSilent` and `clipCount`; the selected index remains configuration metadata and does not establish an observed window. No channel grouping or physical mapping is inferred.

Nullable values have explicit meanings:

- `completedWindowIndex: null`: no completed window is available. Levels, silence state, hold, and Capture clip count are null; this is not silence.
- For Render with no completed window, this all-null rule applies to every channel, selected or unselected. The configured selected channel does not imply silence or level data.
- The Render parser rejects any no-window channel carrying a level, `digitalSilent`, peak hold, or clip count. For completed Render windows, selected-channel peak hold is finite, at least the current intended peak, and accumulates only across completed windows.
- `silent: true` (Capture) or `digitalSilent: true` (unselected Render channel): digital silence is explicitly represented for the completed model window. Window levels are null.
- `silent: false` / selected Render `digitalSilent: false`: finite dBFS levels are present and peak is greater than or equal to RMS.
- Null level fields never encode negative infinity, infinity, or NaN. The client rejects non-finite, out-of-range, malformed, out-of-order, or topology-mismatched data.
- `streamQuality: "UNKNOWN"` and `cleanupEvidence: "NOT_ATTEMPTED"` remain unknown; synthetic lifecycle completion does not turn them into measured quality or device cleanup.

Capture and Render endpoint fixtures independently expose format-dependent topologies of 1, 2, 8, 16, 32, 48, and 64 channels. Capture synthetic windows vary deterministically by stream index, include explicit silent windows, keep session peak hold at the greatest (least negative) completed peak, and use zero synthetic clip counts. Render’s selected channel uses a fixed intended level model; Render peak hold is updated only when a completed window is published. No PCM is created.

## Publication and bounds

The fake service publishes at 10 Hz, below the 20 Hz protocol/UI ceiling. It has no audio callback or PCM path. A dedicated non-audio event publisher owns one replaceable snapshot slot and a 32-entry bounded control queue. Control and terminal events take priority; Stop/terminal publication clears any pending snapshot. Replaced snapshots increment `coalescedSnapshotCount`; `snapshotSequence` gaps are expected. `eventSequence` is assigned only when an event is actually written, so it remains strictly increasing without gaps from worker coalescing. Worker writes use a bounded 5-second pipe write; a failed write fails the operation/transport rather than claiming completion. GUI delivery adds a second bounded control queue (32 entries), one replaceable latest snapshot, and at most one pending Dispatcher drain. The UI reports how many pending snapshots it coalesced; if its control queue overflows, it reports UNKNOWN lifecycle/cleanup instead of claiming terminal success. After UI coalescing, global event-sequence gaps are expected in the displayed subset, while the IPC reader has already validated each received event. Session cursors are removed on terminal events and retained only as a 64-entry tombstone history; active cursors are not evicted. Existing pending requests remain capped at 32.

The integration suite deliberately pauses reads from a real local pipe while the 64-channel session publishes. It observes a positive coalesced snapshot count and then the terminal control event. Memory stays bounded to the control queue, one pending snapshot, and one in-flight write. A slow or disconnected consumer can still hit the write timeout and fail the synthetic operation. No snapshots are sent after Stopping/terminal.

## GUI

Diagnostics reuses the compact 50-DIP channel row for local and Worker Fake readings, exposes eight stream indices per bank, and retains independent Capture/Render format topologies. Logical groups remain metadata and are not rendered as implicit stereo pairs. A completed or stopped session freezes the last snapshot as final. A disconnected worker marks its last values stale and does not fabricate a terminal event or cleanup.

## Validation and results

Run from a Windows x64 Developer PowerShell at the repository root:

```powershell
cmake -S . -B build -A x64
cmake --build build --config Release
dotnet build KlidanAudioProbe.Gui.slnx -c Release
dotnet run --project tests/gui/KlidanAudioProbe.Gui.Core.Tests -c Release
dotnet run --project tests/gui/KlidanAudioProbe.Gui.Worker.Tests -c Release
ctest --test-dir build -C Release --output-on-failure
git diff --check
```

Validation on Windows x64 Release: CMake native build passed; `dotnet build` passed with zero warnings/errors; GUI Core passed 170 checks; Worker IPC/process passed 179 checks, including all Render NO_WINDOW topologies and selected-channel boundaries, malformed no-window payloads, stop/freeze behavior, normal peak hold, delayed pipe-consumer coalescing, and disconnect; full native CTest passed 105/105; relative links and `git diff --check` passed. The Worker test executable starts the local fake worker through a real current-user named pipe. Tests do not enumerate or activate audio devices. Interactive DPI and visual checks require operator verification and are not asserted by the automated tests. Hardware QA was not run.

## C1 integration prerequisites

Before connecting real C1 Capture metering, separately review provenance and physical privacy, map real meter window semantics and stream-quality fields without using the fake model, preserve exact capture lifecycle/cleanup evidence, and add tests proving no raw PCM crosses IPC. This change does not enable live Capture, Render, WASAPI, or hardware QA.
