# 01. Product, users and feature boundaries

## Mission
**KLIDAN Audio Probe** diagnoses the capabilities and behavior of Windows audio endpoints with clear evidence, safe operator-controlled tests and exportable reports. A technically competent user should be able to answer: *which endpoints exist, which formats are reported supported, which actually initialize, which channels contain a signal, and what did the run prove?*

### Primary user journeys
- **Inspect:** open the app, see Capture/Render endpoints and reported metadata without activating an audio stream.
- **Compare formats:** select a single endpoint, review passive Exclusive format matrix; optionally validate Initialize after explicit warning that an endpoint may be temporarily occupied.
- **Measure inputs:** choose Capture, format and duration, accept Capture consent, watch short-window peak/RMS and hold, inspect stream integrity and export evidence.
- **Check outputs (C2 implemented in code):** the CLI offers a confirmed, bounded Render channel test; D2 only presents fake Render settings and session states. Any physical output heard remains a separate operator observation.
- **Share findings:** export sanitized reports with timestamp, method, quality, and limitations, keeping local endpoint IDs private.

### Users
- Sound engineers and technical operators with one or more USB/PCI/virtual audio interfaces.
- Developers diagnosing WASAPI Shared/Exclusive behavior.
- KLIDAN integration maintainers who may later consume advisory device profiles.

### Functional maturity at D0 (2026-10-30)
| Feature | Product truth |
|---|---|
| Passive endpoint discovery/inspection | Implemented in CLI |
| Exclusive format matrix, passive + Initialize-only | Implemented in CLI; real device coverage not yet claimed |
| Capture-only 48 kHz PCM16 mono/stereo meter | Implemented in code; GUI shell has synthetic meter fixtures only |
| C1 channel topology and digital levels | Implemented in code; hardware verification pending; physical socket mapping unknown |
| Render output channel test | C2 implemented in code; GUI shell has synthetic controls only; hardware verification pending |
| Analog RTL and monitor experiments | Existing diagnostic CLI experiments; not polished GUI workflow |
| Device compatibility profiles | Proposal, not implemented |
| Desktop GUI | D1 Architecture A accepted; D2 fake-data shell implemented; worker/IPC integration pending |

### Non-goals for first GUI
- DAW-style mixer, recording, VST processing, live routing/multi-channel ASIO, default device switching.
- Automatic aggressive scan of all connected hardware.
- Calling a driver successful or certified based on one IsFormatSupported result.
- Assuming a test signal is heard merely because its bytes were submitted.

## Success conditions
1. A user can identify a selected endpoint and its test eligibility without knowing its opaque endpoint ID.
2. The entire chain of trust (reported → initialized → started → integrity → physical verification) is visible.
3. Every operation has a clear active/passive label, explicit confirmation when needed and a visible Stop during execution.
4. Exported diagnostic reports preserve facts and unknowns with no fabricated confidence or mapping.
5. The UI shell can be exercised completely with fake snapshots/events and without touching WASAPI.
