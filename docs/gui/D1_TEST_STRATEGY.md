# D1 — Test strategy and D2 prerequisites

**Architecture A accepted 2026-10-30.** D2 ViewModel checks and D3-A1 protocol/process tests are implemented. Audio-service and physical hardware tests remain future work.

## Reuse existing hardware-free seams

Current tests already exercise OS-neutral components such as `ChannelMeter::Meter`, `RenderSignal::Generator`, `ExclusiveFormatMatrix::Api`, report serializers and CLI argument refusal without activating endpoints. D1 should preserve these tests and add service/protocol layers around the same domain behavior rather than replacing it with a second implementation.

## Proposed test layers

| Layer | Test focus | Device access |
|---|---|---|
| Core unit tests | Typed request bounds; format status separation; consent fingerprint; lifecycle classification; meter snapshot/status invariants; sanitized report fields | None |
| Framing/protocol tests | 4-byte length prefix, split/combined pipe writes, max/zero/oversize length, strict UTF-8, BOM, malformed/deep/duplicate JSON, correlation IDs, UUIDs, unknown fields, schema and major/minor negotiation | None |
| Fake worker service tests | Every command contract against fake endpoint/API services; passive query never initializes; Validate initializes but never starts; active consent required; exactly one active operation; Stop and terminal-event ordering | Fake only |
| UI/view-model tests | D0 navigation, endpoint selection, empty/unknown/unsupported, consent cancel, lifecycle vs quality, stale meter, Stop remains visible, WorkerDisconnected is not STOPPED, sanitized export | Fake client only |
| Worker process tests | Child launch/handshake, PID/pipe ACL checks, clean exit, worker crash before and during session, delayed Stop, forced termination, no automatic restart | Fake service; no WASAPI |
| Package tests | UI launches matching adjacent worker; incompatible versions fail before device access; install/uninstall leaves no service or startup entry | No device access |
| D3-A1 process/protocol tests | Real local fake worker; framing/strict JSON; version mismatch; correlation; PID/image/ACL; timeout; idle shutdown; crash/disconnect; Job Object cleanup; fake endpoint/topology validation | Fake only |
| Hardware QA | Separate operator-authorized validation of actual supported modes and physical routing | Explicit later task only; not CI |

## Required fault-injection cases

- Handshake rejects incompatible major or required schema before endpoint enumeration.
- An unknown additive field in a compatible minor is ignored; unknown enum/command, wrong type, duplicate key and oversize frame fail closed.
- `IsFormatSupported` success followed by Initialize failure remains a failure, never `READY`; event setup failure prevents Start.
- Start failure, `AUDCLNT_E_DEVICE_INVALIDATED`, watchdog, stream warning, Stop HRESULT failure and cleanup failure preserve their stages and exact HRESULTs.
- Stop response acceptance is not terminal completion. Delayed/missing terminal events leave the view in STOPPING then UNKNOWN/FAILED according to the observed process/pipe result.
- Worker exits at each state; no disconnect path emits a synthetic successful stop or clean quality.
- Consent is rejected if endpoint, mode, period, format, duration or Render channel changes after dialog confirmation; one receipt cannot start twice.
- A slow pipe/UI coalesces MeterSnapshot events to the latest sequence; event sequence gaps are accepted, older-session/older-sequence snapshots are discarded, stale data is visibly stale, and raw PCM never appears in messages.
- Exported JSON excludes endpoint ID, report path, serial and machine identity; local endpoint selection remains available in the live session.
- Reopening the app after worker failure never automatically reruns the operation.

## D2 entry gate and precise scope

D1 Architecture A was accepted by the owner on 2026-10-30. D2 implements a WPF shell using a fake typed client and deterministic fixture events. It does not launch a native worker, create the Named Pipe, refactor the C++ Probe, access WASAPI, or make device-dependent claims. The UI labels fixture data as demo. Automated ViewModel checks cover navigation, fixtures and lifecycle; the live keyboard, screen-reader, resizing and DPI interaction review remains outstanding.

D3-A1 validates only the idle fake worker boundary and named-pipe process ownership. Before active command implementation, separately validate the native service extraction plan, one-use consent guard, audio session owner-thread cleanup, active Stop timing, report-save behavior, and final .NET/native packaging. D3 remains hardware-free; physical testing is separate and requires explicit authorization.

## D3-A2A results and remaining tests

D3-A2A adds hardware-free local-process tests for fake Capture/Render operation acceptance, response/event interleaving, consent acceptance/rejection/replay, settings fingerprints, direction and channel validation, BUSY, Stop-before-Started, Stop-during-operation, repeated Stop, natural completion, warning/failure/device-loss, unknown cleanup blocking reuse, worker crash during Stop, and GUI shutdown during a fake session. Test seams exercise exact `Int64` event-sequence parsing above the IEEE-754 safe-integer boundary, duplicate terminal events, stale session events, out-of-order sequences and unknown response correlations. Integration uses only the bundled synthetic worker process.

No physical audio API is included in the worker target or exercised here. These checks do not validate WASAPI, COM audio ownership, actual metering, Render PCM generation, hardware Stop timing, real device loss, installer packaging, or physical channel mapping. Desktop visual/DPI interaction remains operator QA.
