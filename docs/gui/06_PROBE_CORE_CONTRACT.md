# 06. Native Probe Core / Desktop UI boundary

## Current fact
The Audio Probe executable is a **native C++ Windows CLI** built with CMake; the KLIDAN main operator application has WPF styles and is a **separate repo**. No reusable Probe GUI-facing API has yet been implemented. Treat the following as a **design proposal**, not an existing ABI.

## Architecture proposal
```text
  Desktop UI (technology ADR, WPF/.NET is the likely first candidate)
         | immutable requests, session IDs, typed events
         v
  UI-facing adapter/service (no audio DSP, no UI parsing of English console text)
         | start / stop / consent / query / snapshots / report
         v
  Native Probe Core (endpoint discovery, format negotiation, audio test lifecycle)
         | Windows WASAPI / MMDevice / MMCSS
         v
  Real audio endpoints
```

### Technology decision to write before GUI
Evaluate two practical options:
- **A. WPF .NET shell + separate native probe process, typed IPC/JSONL**: reuses KLIDAN visual conventions, isolates crashes, but requires explicit IPC schema, cancellation channel and version negotiation. Using stdout parsing from existing human-readable CLI output is forbidden as a permanent contract.
- **B. WPF .NET shell + native DLL with C ABI**: low-overhead snapshots/callbacks, but makes ABI/lifetime/threading and UI crash containment more challenging.

**Suggested initial direction:** WPF shell with a typed adapter and a dedicated native worker process, *only after* a small ADR and safety review. The documents do **not** silently lock this choice. WinUI/Qt may be discussed if concrete requirements justify them.

## Required versioned typed contracts (proposed)
```text
EndpointSummary { localEndpointId, friendlyName, direction, state,
                  mixFormat?, channelMask?, driverMetadata? }
TestRequest { requestId, endpointId, mode, exactFormat,
              periodMode?, mmcss?, durationSeconds?, renderChannel?,
              consentToken?, outputReportPath? }
TestEvent { requestId, monotonicSequence, kind, timestampUtc?, payload }
MeterSnapshot { requestId, channelCount, completedWindowIndex,
                windowPeaksDbfs?, rmsDbfs?, sessionHoldsDbfs?, clips?, quality? }
TestResult { requestId, lifecycle, streamStatus?, streamQuality?,
             observedCounters?, error?, cleanup?, report? }
```

`?` means optional/unknown. IDs are local-only except random reportId. For digital silence use either explicit `silent=true` + `-inf` mathematical value internally or nullable level plus a separate state. JSON must avoid invalid `Infinity` numbers; serializable contracts use `null` plus `silent`/`observed` flags.

### Commands/events
- Commands: `ListEndpoints`, `InspectEndpoint`, `QueryFormatMatrix`, `ValidateFormatMatrix`, `StartCaptureMeter` (C1 code exists), `StartRenderChannelTest` (C2 code exists), `StopSession`, `ExportReport`. These remain proposed UI-facing contracts; no GUI adapter exists.
- Events: `SessionCreated`, `ConsentAccepted` (UI-local), `Preparing`, `Started`, `MeterSnapshot`, `Warning`, `Stopping`, `Stopped`, `Completed`, `Failed`.
- Use monotonically increasing sequence within a session; no event from a stale session can update current views.
- **No active device work in the UI process** unless the architecture ADR deliberately adopts in-process native integration and proves isolation/threading.

## Invariants
- Only one explicitly selected endpoint per passive matrix query; no hidden full-device sweep.
- Capture meter does not activate Render. Render channel test does not activate Capture.
- Test activation requires a fresh mode-specific confirmation; a UI button alone is insufficient without corresponding Core guard.
- Stop semantics reflect actual core cleanup; IPC disconnection must not masquerade as `STOPPED`.
- UI display refresh does not feed timing back into audio threads.
- Real-time callback never performs JSON formatting, file IO, locks or managed callbacks.
- Preserve exact HRESULT values, stage information and provenance. Do not convert missing into success.

## Existing CLI migration
The CLI remains supported as the diagnostic reference. Introduce internal service seams around existing methods before inventing a new test implementation. A GUI prototype can initially use **synthetic fixtures only** and be labelled `Демо`. When the typed boundary is proven, attach the existing native Core in slices. Avoid an always-running daemon unless justified.
