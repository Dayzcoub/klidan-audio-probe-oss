# D3-B2 — Real WASAPI format probe

**Status:** implemented in code; physical endpoint QA not performed.

The operator-facing navigation and layout described below were reorganized in [D3-B2.1](D3_B2.1_OPERATOR_WORKFLOW.md). The worker/native commands and evidence semantics in this document remain the implementation contract.

## Operator workflow

1. Run the WPF app and explicitly connect its bundled worker from Diagnostics.
2. On Devices, explicitly discover Windows endpoints. Select the exact ACTIVE row and verify Capture/Render direction. There is no friendly-name selection or fallback to another device.
3. Choose channel counts (default `1,2`; at most 32 distinct values in `1..64`) and select **Проверить Exclusive-форматы**. The results appear in the separately labeled REAL WASAPI section on Formats.
4. Review each encoding/rate/channel/period result and its HRESULTs. Only `SUPPORTED_REPORTED` means the driver reported exact-format support. It does not mean Initialize succeeded.
5. To initialize one candidate, select its exact row and Default or Minimum period, click **Проверить инициализацию**, read the endpoint/settings warning, check the consent box, and confirm. This may temporarily reserve the selected endpoint. The worker attempts one candidate and one period only.

Neither action starts a stream or sends PCM. Query activates an `IAudioClient` only to call `IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE)` and `GetDevicePeriod`. Initialize-only records `Initialize`, the existing one alignment retry when needed, `GetBufferSize`, `SetEventHandle`, and release evidence; it does not call `Start` or obtain audio service buffers. Do not infer playback, capture, stability, audible output, or physical connector routing from these results.

## Native reuse and endpoint safety

`ExclusiveFormatMatrix` is the shared rule/report layer for the existing CLI and worker. `WasapiApi` in `src/probe/wasapi_format_matrix_api.*` is the shared Windows adapter; the CLI no longer has a second implementation. The worker revalidates the exact ID with `IMMDeviceEnumerator::GetDevice`, checks `DEVICE_STATE_ACTIVE`, and checks `IMMEndpoint::GetDataFlow` immediately before each operation. It only accepts IDs in the latest explicitly requested Windows discovery snapshot. Synthetic IDs, unseen IDs, inactive devices and direction mismatches fail before format work. There is no automatic endpoint substitution.

The query calls `IMMDevice::Activate(IAudioClient)`, `IsFormatSupported` in Exclusive mode and `GetDevicePeriod`. It never calls `Initialize`. Initialize-only first repeats exact support for the chosen format, then invokes the existing bounded initialization sequence. The reusable candidate model supports PCM16, PCM24-in-32 and Float32 at 44,100 and 48,000 Hz, with 1..64 channels when a valid format can be constructed. Default matrix channel counts remain 1 and 2. Explicit larger counts do not imply a speaker layout: no fabricated speaker masks are supplied above stereo. Invalid/unrepresentable candidates are `NOT_TESTED`, not `UNSUPPORTED`.

The COM operation runs on a dedicated MTA thread, not the pipe reader. Only one real diagnostic operation may execute per worker, and it cannot overlap a fake session. Endpoint metadata discovery remains an explicit separate operation. COM interfaces are released on the owning operation thread. Exact HRESULTs, activation, support, Initialize/alignment stages, buffer/event geometry and release evidence remain separate in the result.

## IPC contracts

Both commands use protocol 1.0/schema 1 and are additive handshake capabilities: `RealExclusiveFormatQuery` and `RealExclusiveFormatInitialize`.

Query request:

```json
{
  "commandOrEvent": "QueryFormatMatrix",
  "payload": {
    "endpointId": "exact-local-Windows-endpoint-id",
    "direction": "CAPTURE",
    "channelCounts": [1, 2, 8]
  }
}
```

`channelCounts` is optional; omitted means `[1,2]`. The worker emits six encoding/rate candidates per requested channel count and both period outcomes (up to 192 candidates / 384 outcomes). The response includes `provenance: REAL_WASAPI_QUERY`, `timestampUtc`, exact endpoint identity reference and direction, plus candidate evidence. Each candidate reports encoding/container/valid bits/channel count/channel-mask evidence, activation and support HRESULTs, query status, default/minimum device-period HRESULTs and values, and cleanup/release evidence. `S_OK` maps only to `SUPPORTED_REPORTED`.

Initialize request (only after exact query selection and fresh GUI confirmation):

```json
{
  "commandOrEvent": "ValidateFormatMatrix",
  "payload": {
    "endpointId": "exact-local-Windows-endpoint-id",
    "direction": "RENDER",
    "sampleRate": 48000,
    "channels": 2,
    "encoding": "PCM16",
    "period": "MINIMUM",
    "consentReceipt": {
      "accepted": true,
      "confirmationId": "22222222-2222-4222-8222-222222222222",
      "requestFingerprint": "<SHA-256 of canonical endpoint|direction|rate|channels|encoding|period>"
    }
  }
}
```

The confirmation UUID is one-use. The worker retains at most 4,096 consumed Initialize receipts; after that it rejects additional Initialize attempts until the worker is restarted. The fingerprint binds the exact selected endpoint, direction, format and period. It is an application consent record, not an OS security credential. The response provenance is `REAL_WASAPI_INITIALIZE_ONLY` and contains the support evidence plus initial/final Initialize HRESULT, optional single alignment retry HRESULT and geometry, buffer size, event creation/SetEventHandle/close result, and release-attempt evidence. `INITIALIZED` means Initialize returned success; `READY` additionally requires usable geometry, event readiness and clean cleanup. It never means Start or audio was observed.

Raw endpoint IDs are present in local IPC/UI memory for exact selection and are not shown in the table. This slice does not export a sanitized report; endpoint IDs must not be copied into a shareable report. Result records are local diagnostic evidence, not compatibility certification.

## Timeout and failure behavior

- Before execution, malformed settings, fake/unseen IDs, inactive endpoints, direction mismatch, missing/reused consent and unsupported selections return typed correlated errors.
- Cancellation before a command is written means no operation was requested. After a write may have committed, cancellation/timeout/disconnect makes the outcome unknown; the client does not invent a result or permit reuse of that worker transport.
- The GUI request timeout is bounded at 30 seconds. On timeout/cancellation after a possible write, the GUI terminates the worker it owns. A blocked COM driver call cannot be assumed safely interruptible; terminating the process is the bounded escalation. No automatic retry or reconnect is performed.
- A worker process loss during a native call leaves the operation result/cleanup unknown. Do not interpret it as a clean release or successful Initialize.
- No Start, Capture/Render service acquisition, PCM transfer, signal generation, or automatic endpoint scan is part of these operations.

## Verification

Automated checks use an injected native `Api` seam and a separately named test worker. The test worker has no MMDevice or WASAPI calls and returns `TEST_FAKE_FORMAT_BACKEND`, never real provenance. It exercises real local Named Pipe framing/dispatch, Capture and Render query results, direction/endpoint rejection, unsupported/activation/busy statuses, Initialize failure, one alignment retry, event readiness and cleanup failures, one-use consent, and explicit no-Start/no-audio-transfer evidence. Native unit tests cover shared candidate and single-candidate execution. GUI Core tests cover endpoint selection, query result provenance, consent cancel/accept/fingerprint, stale results, and the GUI command/result bindings. The final validation passed: Windows x64 Release native build, Windows x64 Release GUI build (0 warnings/errors), CTest 106/106, GUI Core 261 checks, and worker IPC/process integration 199 checks. These checks do not prove a real driver behaves correctly.

Validation commands:

```powershell
cmake --build build --config Release
cmake --build build --config Release --target probe_worker_format_fixture
dotnet build KlidanAudioProbe.Gui.slnx -c Release
dotnet run --project tests/gui/KlidanAudioProbe.Gui.Core.Tests -c Release --no-build
dotnet run --project tests/gui/KlidanAudioProbe.Gui.Worker.Tests -c Release --no-build
ctest --test-dir build -C Release --output-on-failure
git diff --check
```

The production worker is `build/worker/Release/KlidanProbeWorker.exe`; the test-only fixture is `build/worker-tests/Release/probe_worker_format_fixture.exe`; the GUI is `src/gui/KlidanAudioProbe.Gui.App/bin/Release/net10.0-windows/KlidanAudioProbe.Gui.App.exe` (with the bundled `KlidanProbeWorker.exe` beside it). No real endpoint QA or interactive visual/DPI QA is claimed here.

## Physical QA checklist and next boundary

Before operator use, confirm the intended Windows endpoint and direction, close applications that may own it, and understand that Initialize may reserve it briefly. Run passive query first and save/review exact HRESULTs. Initialize only one explicitly selected candidate/period with a fresh confirmation. Verify the worker returns and the endpoint becomes usable afterward. Do not run this on an endpoint whose temporary exclusive reservation is unacceptable. Record the actual endpoint/driver conditions separately; do not assign a guessed driver version or claim physical loopback.

## D3-B2.1a workflow consistency

Initialize-only now exposes the passive query as a prerequisite in the same Test step. The selected mode remains Initialize-only after querying; format/period selection and fresh one-use consent are still explicit. Query failure does not allow a prior successful matrix to act as current evidence.

GUI grouping validates the exact requested encoding/rate/channel set and requires one record per `default` and `minimum` period. Shared support status, support HRESULT, activation/period/query-cleanup evidence must agree. Missing, duplicate, unexpected, or contradictory period evidence is represented as `INCOMPLETE/CONFLICT`; it cannot be selected for Initialize-only. Query matrices remain tied to their exact endpoint ID and direction. Endpoint selection changes and rediscovery invalidate prior query authorization, even if the operator later selects the earlier endpoint again. No IPC schema or native WASAPI behavior changed.

Regression coverage includes first-launch Initialize-only prerequisite, stale query, query success without automatic Initialize, cancelled consent, malformed decoded query response, missing/duplicate periods, inconsistent support status/HRESULT, failed refresh after success, endpoint-bound result history, consent invalidation, and navigation retaining endpoint/format/period. These are GUI-side synthetic/injected-response checks; they are not real endpoint tests.

The next real-audio stage would require a separate reviewed scope for live Capture/Render lifecycle, bounded PCM handling, cancellation, stream quality, and physical safety. D3-B2 does not provide that capability.

## D3-B2.2 operator-reported QA context

The operator reported a successful Audient iD4 Capture Initialize-only result for PCM16, 48,000 Hz, stereo, DEFAULT: `IsFormatSupported` S_OK; `Initialize` S_OK; requested period 10 ms; 480-frame buffer; `SetEventHandle` S_OK; event readiness true; cleanup `RELEASE_ATTEMPTED`. The operator also reported no `Start` and no PCM. This observation was not reproduced or hardware-tested in the D3-B2.2 UX task; `RELEASE_ATTEMPTED` is not cleanup confirmation.
