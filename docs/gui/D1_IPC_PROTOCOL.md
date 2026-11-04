# D1 — Local IPC protocol

**Architecture A accepted 2026-10-30.** D3-B1 adds explicit read-only Windows endpoint metadata, and D3-B2 adds exact Exclusive format queries plus consent-gated Initialize-only validation. Capture, Render, and report operations remain otherwise as described below; see [D3-B1](D3_B1_IMPLEMENTATION.md) and [D3-B2](D3_B2_IMPLEMENTATION.md).
Transport: local Windows Named Pipe. D3-A1 had no Windows endpoint discovery; D3-B1 adds only the explicit passive metadata command below. Neither path opens an audio stream.

## Connection and framing

- The WPF application creates one per-launch pipe with an unpredictable UUID suffix, e.g. `\\.\pipe\KlidanAudioProbe.<uuid>`, before launching its worker.
- The pipe is local only: create with a DACL granting the current user SID, set `PIPE_REJECT_REMOTE_CLIENTS`, use first-instance protection, and verify the connecting client PID matches the child process the UI launched. Pipe-name secrecy is not an authorization mechanism.
- Use a byte-mode Named Pipe with explicit framing: 4-byte unsigned little-endian payload length followed by exactly that many UTF-8 JSON bytes. The prefix is not included in the length.
- Maximum payload: **1,048,576 bytes (1 MiB)**. Reject zero/oversize, truncated, invalid UTF-8, BOM-prefixed, non-object, duplicate-key, excessive-depth (>32) or malformed JSON frames before dispatch. Do not allocate based on an unchecked length.
- No line-delimited stdout protocol and no parsing of CLI text. No PCM/audio data crosses this boundary.

D3-A1 implements framing in the WPF client and worker. The worker uses RapidJSON 1.1.0 for strict JSON parsing and duplicate-key rejection, then validates UTF-8, object root, depth and frame size before dispatch. Typed errors preserve failure stage and error code. The bundled source is from [Tencent RapidJSON](https://github.com/Tencent/rapidjson), under its included MIT license; `src/third_party/rapidjson/license.txt` records the license, and `bin/jsonchecker` is excluded.

## Version negotiation and common envelope

First frame uses the fixed bootstrap schema:

```json
{
  "bootstrapVersion": 1,
  "kind": "request",
  "requestId": "11111111-1111-4111-8111-111111111111",
  "command": "Handshake",
  "payload": {
    "protocol": {"major": 1, "minMinor": 0, "maxMinor": 0},
    "schemas": {"command": [1], "event": [1], "report": [1]}
  }
}
```

The worker responds with the selected protocol minor, schema versions, worker version and capability list. A protocol-major or required-schema mismatch is rejected before endpoint access. After negotiation, every frame uses:

The implemented bootstrap validator requires `bootstrapVersion`, `kind=request`, `command=Handshake`, a valid request UUID, a supported protocol major/minor range, and offered command/event/report schema lists containing schema 1. The worker does not enter its command loop or advertise negotiation success when any required value is missing or incompatible. A trustworthy request UUID is echoed in the bounded typed handshake error; an absent or malformed UUID is never used as correlation.

```json
{
  "protocolVersion": {"major": 1, "minor": 0},
  "schemaVersion": 1,
  "kind": "request | response | event",
  "requestId": "uuid",
  "correlationId": "uuid-or-null",
  "sessionId": "uuid-or-null",
  "eventSequence": 0,
  "commandOrEvent": "typed-name",
  "payload": {}
}
```

`requestId` is a new UUID on each request; response/event `requestId` is null. A response echoes the request ID in `correlationId`. The worker creates a UUID `sessionId` for an accepted asynchronous validation or active test and returns it in the acceptance response. Request/response `eventSequence` is zero; every worker event has a connection-wide, strictly increasing `eventSequence` starting at 1. Meter payloads also carry the core snapshot sequence. Event gaps are allowed because snapshots may be coalesced.

Unknown optional fields are ignored within the negotiated major/schema. Missing required fields, invalid types, unknown required enum values, duplicate JSON keys and invalid bounds are rejected. The D3-A1 worker returns `UNKNOWN_COMMAND` for unknown command names; no field is interpreted as a shell command, executable, plugin, or arbitrary method name. A newer major or unsupported required schema fails closed with a version error. Additive minor-version changes must not change existing field meaning.

For every post-handshake request the worker validates protocol major/minor, schema version, request kind and UUID, bounded command name, null client correlation/session IDs, zero request event sequence, and object payload before dispatch. A trustworthy request UUID receives a correlated typed rejection. If request identity is missing or malformed, the worker returns a bounded envelope error and closes the connection; invalid envelopes never reach the operation service. The GUI validates the same required envelope members on all responses/events before dispatch.

## Command contracts

All command and response payloads use schema version 1. HRESULTs are signed 32-bit integers or canonical hex strings; absent observations are `null`, never fabricated success/zero.

| Command | Required payload | Behavior and result |
|---|---|---|
| `Handshake` | Supported protocol major/minor range and supported command/event/report schema versions | **Implemented:** negotiates protocol 1.0 and schema 1 before commands; incompatibility fails closed. |
| `ListEndpoints` | `direction: CAPTURE\|RENDER\|BOTH` | **Implemented, synthetic only:** returns fake Capture/Render entries, without real endpoint IDs or device access. Channel counts/topologies are synthetic and format-dependent. |
| `ListWindowsEndpoints` | `{}` | **Implemented, explicit and read-only:** enumerates Windows MMDevice endpoints and reads endpoint properties. Response provenance is `REAL_WINDOWS_METADATA`; this does not query format support or initialize an audio client. Worker advertises this additive capability during protocol 1.0 handshake; clients that do not recognize it can ignore it. |
| `InspectEndpoint` | `endpointId`, expected direction | **NOT_IMPLEMENTED.** Further targeted endpoint inspection. |
| `QueryFormatMatrix` | exact `endpointId`, direction; optional explicit `channelCounts` (1..32 unique values, each 1..64) | **Implemented, explicit real operation:** revalidates the exact active endpoint/direction, then queries Exclusive support for PCM16, PCM24-in-32 and Float32 at 44.1/48 kHz. Default channel counts are 1 and 2. Calls `Activate`, `IsFormatSupported(EXCLUSIVE)` and `GetDevicePeriod`; never `Initialize`, `Start` or transfer PCM. Results use `REAL_WASAPI_QUERY`. |
| `ValidateFormatMatrix` | exact endpoint/direction, one format, one `DEFAULT\|MINIMUM` period, one-use `consentReceipt` | **Implemented, explicit Initialize-only:** requires a prior exact `SUPPORTED_REPORTED` result in the UI and fresh consent bound to endpoint/direction/format/period. Revalidates the endpoint immediately before execution; performs only the existing single alignment retry when required; captures buffer/event and cleanup evidence. Never calls `Start` or transfers PCM. Results use `REAL_WASAPI_INITIALIZE_ONLY`. |
| `StartCaptureMeter` | `endpointId`, format, period, duration, `consentReceipt` | **FAKE ONLY.** Produces synthetic MeterSnapshot events; no endpoint access or PCM. |
| `StartRenderChannelTest` | `endpointId`, format, selected Windows channel/group, `consentReceipt` | **FAKE ONLY.** MeterSnapshot models intended output, not DAC or physical playback. |
| `StopSession` | `sessionId`, `reason` | Stops the synthetic operation and emits fake lifecycle terminal events; it does not stop audio. |
| `ExportReport` | `reportId`, `privacyMode: SANITIZED`, supported report schema | **NOT_IMPLEMENTED.** No report export crosses this worker boundary. |

`ListWindowsEndpoints` is dispatched only after this typed command is received. Handshake, worker startup, and fake `ListEndpoints` do not enumerate Windows devices. The response includes endpoint IDs because selection/diagnostics are local; the GUI does not render those IDs. Format operations require an ID from the most recent explicit discovery response, reject fake identities, and re-read device state and data flow immediately before the native operation. `Shutdown` returns a correlated response and exits. Other known unimplemented D1 commands return `NOT_IMPLEMENTED`; unknown names return `UNKNOWN_COMMAND`.

`QueryFormatMatrix` and `ValidateFormatMatrix` run on a dedicated MTA operation thread, separate from the pipe reader. One real diagnostic operation per worker is allowed. A client timeout after a request may have been written terminates the GUI-owned worker; its operation outcome is unknown. A blocked COM driver call is not claimed to be safely interruptible. Initialize-only can temporarily reserve an endpoint, so the GUI requires a fresh one-use consent receipt bound to immutable settings. See [D3-B2 implementation and operator procedure](D3_B2_IMPLEMENTATION.md).

## Channel topology contract

Capture and Render topologies are independent and format-dependent. Each topology describes 1..64 ordered digital stream channels with stable one-based indices for that format. Optional logical groups (`MONO`, `STEREO_PAIR`, `GROUP`, `UNKNOWN`) are explicit metadata; adjacent channels are never paired implicitly. Optional `speakerMask` is separate and is not a complete mapping for high channel counts. Unknown roles and physical routes stay unknown. Group metadata does not change sample order, imply downmix/duplication, or establish physical connector mapping. D3-A1 responses are synthetic fixtures only.

`consentReceipt` is `{accepted:true, confirmationId:<UUID>, requestFingerprint:<SHA-256>}`. The UI creates it only after the mode-specific dialog. The worker independently canonicalizes and checks endpoint, mode, format, channel, period and duration against the fingerprint and consumes it once. This binds confirmation to settings but is not an OS security credential and does not replace native mode guards.

An accepted active command is not `Started`. Its responses/events must preserve:

- Format support vs Initialize vs Start as separate facts and HRESULTs.
- Operation lifecycle (`PREPARING`, `READY`, `STARTED`, `STOPPING`, `COMPLETED`, `FAILED`, `CANCELLED`, `UNKNOWN`) vs stream quality (`CLEAN`, `ANOMALIES_PRESENT`, `SEVERE_ANOMALIES`, `UNKNOWN`).
- Sample-level digital Render evidence vs physical playback/audibility (always unmeasured by this protocol).
- Local endpoint identity used for commands vs sanitized exported report identity.
- Unsupported vs unknown/unavailable data; unknown numeric fields are `null`.

Example active request and acceptance response (acceptance is not `Ready` or `Started`):

```json
{
  "protocolVersion": {"major": 1, "minor": 0}, "schemaVersion": 1,
  "kind": "request", "requestId": "44444444-4444-4444-8444-444444444444",
  "correlationId": null, "sessionId": null, "eventSequence": 0,
  "commandOrEvent": "StartCaptureMeter",
  "payload": {
    "endpointId": "local-only-endpoint-id", "sampleRateHz": 48000,
    "format": "PCM16", "channels": 2, "period": "DEFAULT",
    "mmcss": "OFF", "durationSeconds": 5,
    "consentReceipt": {
      "accepted": true,
      "confirmationId": "66666666-6666-4666-8666-666666666666",
      "requestFingerprint": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
    }
  }
}
```

The response echoes the request correlation and allocates the session. The worker queues this response before events for that session:

```json
{
  "protocolVersion": {"major": 1, "minor": 0}, "schemaVersion": 1,
  "kind": "response", "requestId": null,
  "correlationId": "44444444-4444-4444-8444-444444444444",
  "sessionId": "55555555-5555-4555-8555-555555555555",
  "eventSequence": 0, "commandOrEvent": "StartCaptureMeter",
  "payload": {"status": "ACCEPTED", "lifecycle": "PREPARING"}
}
```

## Typed event contracts

Events carry `sessionId`, `eventSequence`, and the initiating request `correlationId` where available.

| Event | Key payload |
|---|---|
| `Preparing` | Operation kind, requested settings, selected endpoint reference (local-only), consent receipt accepted/validated. |
| `Ready` | Initialize HRESULT, actual format/buffer/period, event readiness and cleanup/setup evidence; Start has not yet been called. |
| `Started` | Exact Start HRESULT and monotonic start time. Emitted only after successful Start. |
| `MeterSnapshot` | Snapshot sequence, channel count, completed-window index, latest completed-window peak/RMS/silence, session peak hold and clips; no PCM. |
| `Warning` | Stable warning code, stage and structured evidence. Never turn a warning into a clean result. |
| `Stopping` | Stop reason and whether cancellation was accepted; does not imply Stop succeeded. |
| `Completed` | Terminal lifecycle, duration/frame coverage, stream quality, exact Stop HRESULT, cleanup evidence and report reference. |
| `Failed` | Terminal lifecycle, failure stage/HRESULT (or unknown), partial evidence, stream quality and cleanup state. |
| `WorkerDisconnected` | UI-local event with last received event sequence, disconnect reason and optional observed process exit code; current nonterminal session result and cleanup are unknown. |

Example bounded meter event:

```json
{
  "protocolVersion": {"major": 1, "minor": 0}, "schemaVersion": 1,
  "kind": "event", "requestId": null,
  "correlationId": "22222222-2222-4222-8222-222222222222",
  "sessionId": "33333333-3333-4333-8333-333333333333",
  "eventSequence": 7, "commandOrEvent": "MeterSnapshot",
  "payload": {
    "snapshotSequence": 42, "completedWindowIndex": 16,
    "windowDurationMs": 100,
    "channels": [
      {"index": 1, "windowPeakDbfs": -18.2, "windowRmsDbfs": -26.4,
       "silent": false, "sessionPeakHoldDbfs": -15.1, "clipCount": 0},
      {"index": 2, "windowPeakDbfs": null, "windowRmsDbfs": null,
       "silent": null, "sessionPeakHoldDbfs": null, "clipCount": null}
    ],
    "streamQuality": "CLEAN"
  }
}
```

Here null means no completed/observed value; it is not silence. An observed silent window uses `silent:true` and a documented level representation (JSON-safe nullable dBFS plus the silence flag), never invalid JSON `Infinity`.

The pipe has a dedicated non-audio writer that serializes whole frames so responses and events cannot interleave. Keep control/terminal events in a bounded queue (proposed cap: 128 messages); overflow fails the session/connection visibly instead of allocating an unbounded backlog. Meter data uses one replaceable latest-snapshot slot, not this queue. The UI does not acknowledge each meter event.

## Backpressure and privacy

The audio servicing thread only updates bounded native meter state. A non-audio publisher copies `ChannelMeter::Snapshot` and formats/sends IPC. It publishes at 10–20 Hz; details and stale behavior are in [D1 lifecycle](D1_LIFECYCLE.md). Pipe writes use bounded overlapped I/O/timeouts. No per-sample messages, PCM, or acknowledgement waits occur on the servicing thread.

Local endpoint IDs may appear in endpoint-list/inspect/test request traffic because they are needed to select the exact endpoint. They remain worker/UI session data only. `ExportReport` uses the existing sanitized report contract and must omit endpoint ID, user path, serial and system identity. Physical connector mapping remains `UNKNOWN`; a Render event does not prove DAC playback.

## D3-A2A implementation status

The fake-only session commands are implemented in `D3_A2A_IMPLEMENTATION.md`. The worker echoes the original command token for `UNKNOWN_COMMAND`, so its response remains correlated and is surfaced as a typed command rejection without closing the pipe. Known but unimplemented D1 commands still return `NOT_IMPLEMENTED`.

The post-handshake GUI client has one asynchronous frame reader. It dispatches responses by `correlationId` and accepted session events by `(sessionId, eventSequence)`. The worker writes the accepted Start response before starting its event producer. Event sequences are unsigned 64-bit decimal JSON integers; the client reads them as `Int64`, without floating-point conversion. Duplicate terminal events and stale session events are reported and ignored; invalid non-monotonic sequences fail the transport. A late/unknown response correlation is reported separately and does not become success.

The fake event set includes `Preparing`, `Ready`, `Started`, `MeterSnapshot`, optional `Warning`, `Stopping`, and one of `Completed` or `Failed`. Synthetic meter payloads have separate Capture and Render schemas, finite-or-null levels, exact Int64 sequences, and no PCM. They are model values, not hardware observations. See [D3-A2B implementation](D3_A2B_IMPLEMENTATION.md) for null semantics and bounds. Real Initialize/Start HRESULTs and audio frames are not emitted.

Start request lifecycle is distinct from operation lifecycle. Before any bytes may have been written, cancellation removes the request. After a write may have committed, timeout/cancellation reports an unknown Start outcome while retaining its bounded correlation entry. A late acceptance emits the actual session UUID, original correlation UUID, immutable settings, and worker operation state; a late rejection clears the unknown result with its exact code. At most 32 request correlations are retained; reaching that bound rejects another request. Response dispatch and timeout/cancellation transition under the same pending-request state lock, so one side wins atomically. Pipe loss leaves any unresolved Start and cleanup `UNKNOWN`, and this one-shot worker is not restarted in the same GUI process.
