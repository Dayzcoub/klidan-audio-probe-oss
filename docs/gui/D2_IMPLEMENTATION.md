# D2 — WPF desktop shell (synthetic fixtures)

**Status:** implemented as a runnable shell against fake data only. This is not a Probe Worker, IPC, WASAPI, endpoint discovery, Capture, or Render implementation.

## Projects

- `src/gui/KlidanAudioProbe.Gui.Core`: OS-neutral DTOs, `IProbeClient`, deterministic `FakeProbeClient`, and MVVM state/commands.
- `src/gui/KlidanAudioProbe.Gui.App`: WPF `net10.0-windows` x64 application; resources map to the approved Design Kit v0.1 tokens. `app.manifest` requests Per-Monitor-V2 DPI awareness.
- `tests/gui/KlidanAudioProbe.Gui.Core.Tests`: executable STA assertions for ViewModel transitions, WPF bindings, and fixtures; no external test framework is required.

The WPF window uses reusable token-backed styles, a custom title bar, six Russian navigation screens, a bottom demo status bar, a narrow-window inspector collapse, and a per-mode synthetic consent dialog. A window timer drives the demo meter and bounded fake session. The fake client can show endpoint-empty and device-selected fixtures, format statuses/HRESULTs, report records, warning, device loss, failure, worker disconnect, cancellation, and loud/silent meter windows. Every screen visibly carries **«ДЕМО · Данные симуляции»**. No active session starts on launch. A disconnected worker state is `UNKNOWN`; completed lifecycle and stream quality remain separate.

The test seam is `IProbeClient`; a future typed IPC client can replace `FakeProbeClient`. No IPC client exists in D2. The shell does not start the native worker, create pipes, access audio endpoints, play sound, capture audio, or save raw PCM.

## Build and run

From the repository root in PowerShell:

```powershell
dotnet build KlidanAudioProbe.Gui.slnx -c Release
dotnet run --project tests/gui/KlidanAudioProbe.Gui.Core.Tests/KlidanAudioProbe.Gui.Core.Tests.csproj -c Release
```

Run the built desktop application:

```powershell
.\src\gui\KlidanAudioProbe.Gui.App\bin\Release\net10.0-windows\KlidanAudioProbe.Gui.App.exe
```

The app targets x64 and uses the installed .NET 10 desktop runtime. No installer or self-contained deployment is included.

## Limits and next boundary

- The window dimensions, keyboard focus styles, Per-Monitor-V2 manifest, and inspector collapse are implemented. A live visual/DPI interaction check was not completed in the automated environment.
- Fake format and report fixtures demonstrate UI states only. They do not attest to real endpoint support, initialized streams, Capture quality, physical playback, or acoustic measurements.
- D2 does not establish the worker process lifecycle, consent guard, Named Pipe protocol, JSON serialization, or report export. Those belong to a later worker/IPC slice under accepted ADR-002.
- Approved Design Kit tokens and source assets were not modified.

## D2.1 visual and UX corrections

- ComboBox closed, popup, item, focus, hover, selected, disabled and scrollbar visuals use the approved dark token brushes. Render channel and duration options carry `int` values through `SelectedValuePath=Value`; no string-tag conversion is used.
- The formats screen is a virtualized selectable DataGrid with 12 candidates and 24 period results. Status text includes an icon and explicit `SUPPORTED_REPORTED`, `UNSUPPORTED`, `FAILED`, `UNKNOWN / NOT_TESTED`, `INITIALIZED`, or `READY` label; selecting a row shows both HRESULTs and clarifies Initialize evidence.
- Endpoint and report lists use left-aligned selectable rows. The contextual inspector follows the selected entity/current screen. Demo endpoint selection is explicit and mode-specific; active session settings are immutable.
- Diagnostics displays a 200-entry observable history of typed synthetic events with increasing sequence, timestamp, session UUID and synthetic provenance. A worker disconnect sets lifecycle/cleanup evidence to unknown and adds no Stop/Stopped event.
- Automated ViewModel and WPF binding checks pass. Interactive verification at 1024×720, 1280×800 and 1440×900 DIP and at 100%, 125%, 150% and 200% Windows scaling remains pending operator verification.

## D2.2 final polish and synthetic lifecycle QA

- The format matrix uses short Russian status labels and fixed 42-DIP rows. Each period cell exposes the full machine status, `IsFormatSupported` and `Initialize` HRESULTs, and provenance through accessibility help text; the selected-format inspector retains the same evidence. `UNKNOWN` and `NOT_TESTED` remain separate.
- An empty Capture or Render fixture hides the list and shows a restore action. Restoring fixtures does not auto-select endpoints; Capture and Render selections remain independent, and unavailable fixtures cannot start a session.
- Operator-facing labels use Russian copy while preserving technical names such as Capture, Render, `IsFormatSupported`, HRESULT, PCM, and Windows channel index.
- The synthetic Capture workflow now alternates signal and silence snapshots. Stop is single-shot, shows `STOPPING` before `STOPPED`, and records no cleanup success. Session identity guards timer completion; disconnect leaves lifecycle and cleanup unknown and blocks another session.
- Synthetic generic warnings and failures carry no fabricated WASAPI HRESULT. `AUDCLNT_E_DEVICE_INVALIDATED` is emitted only by the explicit device-loss scenario.
- GUI Release build and deterministic STA checks cover the fake workflow, empty/restore states, accessible matrix evidence, event order, timer identity, and timer shutdown on window close. Native Release CTest passed 104/104.
- Interactive UI automation confirmed the empty/restore device state, matrix accessibility descriptions and all 12 rows, Capture consent/Started presentation, and visible Stop control. The bounded 3-second session completed during observation. DPI scaling and the requested 1024×720, 1280×800, and 1440×900 DIP layouts remain pending operator verification; current-session interaction does not establish those behaviors.
