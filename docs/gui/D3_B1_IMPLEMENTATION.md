# D3-B1 — Passive Windows endpoint discovery

**Status:** implemented, read-only metadata only. No audio hardware test was run for this change. See [D1 IPC protocol](D1_IPC_PROTOCOL.md), [D1 lifecycle](D1_LIFECYCLE.md), and [D3-A2B snapshot contract](D3_A2B_IMPLEMENTATION.md).

## Implementation

The WPF Devices page has three visibly separate sources: Local Demo, Worker Fake, and Windows Devices. Windows discovery is a deliberate operator command and is disabled until the connected worker advertises the `ListWindowsEndpoints` capability. There is no startup or background scan. Results use the `REAL_WINDOWS_METADATA` provenance; existing fake endpoint listing remains `FAKE_SYNTHETIC`. A disconnected worker's last Windows list is retained as historical evidence and is not current availability. The GUI shows friendly name, direction, Windows device state, form factor, Shared Mode summary when parseable, reported channel count, and physical routing as unknown. It does not show endpoint IDs or offer active tests.

The native read-only service in `src/worker/windows_endpoint_discovery.*` uses:

- `CoInitializeEx(COINIT_MULTITHREADED)` and `CoUninitialize` on the same command thread.
- `CoCreateInstance(MMDeviceEnumerator)`, `IMMDeviceEnumerator::EnumAudioEndpoints` for Capture and Render with `DEVICE_STATEMASK_ALL`, `IMMDeviceCollection::GetCount` / `Item`, `IMMDevice::GetId` / `GetState`, and `IMMDevice::OpenPropertyStore(STGM_READ)`.
- `IPropertyStore::GetValue` for `PKEY_Device_FriendlyName`, `PKEY_AudioEndpoint_FormFactor`, and `PKEY_AudioEngine_DeviceFormat`.

COM interfaces, `PROPVARIANT`s, and `GetId` memory use local RAII owners. Endpoint/property strings are bounded to 32,768 UTF-16 code units and the shared-format blob to 64 KiB before copying. The bounded format blob is parsed without activation. Failures retain HRESULT and stage/evidence; collection failures produce partial results, while unavailable individual properties remain attached to their endpoint. An injected `Source` seam exercises discovery and serialization without a Windows device query.

## Format and topology meaning

`PKEY_AudioEngine_DeviceFormat` describes the Windows shared-mode engine mix format. It is reported as metadata only. It does not establish Exclusive support, `IsFormatSupported`, successful `Initialize`, the channel count available in another mode, ASIO capability, or physical connector layout.

The parser validates the complete `WAVEFORMATEX` size and extension length before reading. `WAVEFORMATEXTENSIBLE` additionally requires its complete extension. It reports PCM, IEEE float, or unknown encoding, exact reported channel count, rate, container/valid bits, and optional speaker mask. A nonzero mask is marked valid only when its set-bit count matches the reported channel count. Counts above the existing 64-channel topology limit are preserved as exact metadata and marked `UNSUPPORTED_CHANNEL_COUNT`; no channel array is synthesized or truncated. No grouping or physical mapping is inferred.

## IPC and safety boundary

`ListWindowsEndpoints` is a new capability advertised additively on the existing protocol 1.0 handshake. Older clients can ignore the unknown capability; this GUI only sends the command when it was advertised. The operation is separate from synthetic `ListEndpoints`. Fake Capture/Render start validation accepts only the existing `fake-*` IDs and rejects Windows endpoint IDs.

This command does not call `IMMDevice::Activate`, `IAudioClient::IsFormatSupported`, `IAudioClient::Initialize`, `IAudioClient::Start`, Capture/Render service APIs, or any PCM path. It runs only in response to the explicit Devices-page action. It does not enumerate on worker startup, handshake, or fake listing.

Endpoint IDs are present in local worker/UI protocol data for future exact selection but are not shown in the UI and are not included in sanitized reports. No network transmission is introduced. Names and other endpoint properties are driver-supplied local metadata and must be treated as untrusted display strings.

## Automated coverage and results

The native injected-source tests cover empty and failed listings; Capture-only/Render-only/mixed fixtures; duplicate names; active, disabled, unplugged and not-present states; independent property/HRESULT failures; disappearance during iteration; PCM, float and extensible format parsing; truncated and malformed blobs; channel counts 1/2/8/16/32/48/64 and above 64; invalid/unknown masks; and exact provenance serialization.

GUI Core tests cover explicit-action-only discovery, capability-gated compatibility, metadata display, failed refresh, and retention as historical data on disconnect. Worker-process tests exercise the typed IPC DTO through a local scripted fake worker and verify that the active fake service rejects a Windows endpoint ID. These tests do not call MMDevice APIs.

Validated on Windows x64 Release:

```powershell
cmake --build build --config Release
dotnet build KlidanAudioProbe.Gui.slnx -c Release
ctest --test-dir build -C Release --output-on-failure
dotnet run --project tests/gui/KlidanAudioProbe.Gui.Core.Tests -c Release --no-build
dotnet run --project tests/gui/KlidanAudioProbe.Gui.Worker.Tests -c Release --no-build
git diff --check
```

Results: native Release build passed; GUI Release build passed with zero warnings/errors; CTest passed 106/106; GUI Core passed 226 checks; worker IPC/process integration passed 182 checks. No physical or real endpoint QA is claimed.

## Manual read-only QA

For an operator smoke test, start the GUI and explicitly start/connect the bundled worker from Diagnostics. Open Devices and use **Обнаружить устройства Windows**. Confirm that the list separates Capture and Render, identifies Windows device states, shows Shared Mode format only when available, labels physical routing unknown, and contains no active-test controls. Disconnect the worker and confirm the list is labeled historical. This procedure performs passive Windows metadata enumeration only. Do not start Capture/Render or route signal as part of this check.

## D3-B2 follow-up

Exact Exclusive support queries and consent-gated Initialize-only validation are implemented in [D3-B2](D3_B2_IMPLEMENTATION.md). D3-B1 discovery remains passive metadata only; it does not establish format support, successful Initialize, Start, observed audio, stability, or physical verification.
