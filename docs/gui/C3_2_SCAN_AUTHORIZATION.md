# C3.2 — Worker Scan Authorization Foundation

## Scope and state

C3.2 adds a connection-local authorization contract to the native Worker and fixes Duplex profile operation identity. It does not add GUI controls or enable `ScanExecutionMode.RealWorker`. No WASAPI engine, quality threshold, or physical QA behavior changes.

The Worker remains authoritative for scan authorization. `RegisterScanSession` accepts an immutable plan only after explicit acceptance, validates its bounds and exact current-discovery endpoint IDs, recomputes the plan fingerprint, and consumes the approval UUID once. It returns an opaque Worker-generated UUID. The token has no meaning on another Worker connection or process.

## Protocol contract

`RegisterScanSession` payload fields:

- `endpoints`: one or two distinct `{ endpointId, direction }` entries from the latest active Windows discovery.
- `allowedOperations`: a unique allowlist of `QueryFormatMatrix`, `ValidateFormatMatrix`, `ValidateStream`, and `ValidateDuplex`.
- `formats`: up to 32 exact `{ sampleRateHz, encoding, channels, periods, mmcssModes }` entries. Supported metadata candidates are 44.1/48 kHz PCM16, PCM24-in-32, or Float32 and 1..64 channels. Current Stream/Duplex execution remains the existing 48 kHz stereo PCM16 implementation.
- `lifetimeSeconds`: 1..600; `maximumOperationDurationSeconds`: 1..10; `maximumOperationCount`: 1..24.
- `planFingerprint`: lowercase/uppercase-insensitive SHA-256 hex of the canonical plan below.
- `authorization`: `{ accepted: true, confirmationId: <UUID> }`.

Canonical fingerprint UTF-8 input is built by the Worker from validated values:

```text
C3.2|<lifetime>|<max-duration>|<max-count>\n
E|<direction>|<UTF-8-byte-count>|<exact-endpoint-id>\n   (sorted ordinally)
O|<command>\n                                     (sorted ordinally)
F|<rate>|<encoding>|<channels>|<period>|<mmcss>\n (expanded and sorted ordinally)
```

The approval UUID has bounded one-use history (128 registrations per connection). Every active request must include `scanSessionId` and a unique `scanOperationId`; Stream/Duplex also require that it equals the report `operationId`. The Worker checks token, expiry, discovery generation, endpoint direction and identity, operation allowlist, exact format/period/MMCSS scope, duration, count, duplicate identity, and exclusive-operation serialization. Unknown plan fields fail closed.

Session operations return through the existing structured Query, Initialize-only, Stream, and Duplex responses. Existing manual initialization/stream/Duplex confirmation receipts remain unchanged and cannot be synthesized or reused by the scan-session path. While a valid scan session is open, unscoped real operations are rejected; close or cancel the session before returning to manual operation mode.

No scan plan can authorize test tones, physical routing, RTL, or AudioHost activation: those are not operation enum values. Fake endpoint IDs are rejected. Session authorization does not claim that an operation succeeded; its ordinary result/report remains authoritative evidence.

## Invalidation and cleanup

- A new explicit Windows discovery invalidates the session and increments its discovery generation.
- `CancelScanSession` invalidates the token and signals the existing Stream/Duplex cancellation event when one is active. Query and Initialize-only driver calls are not safely interruptible; the session is invalid immediately, but the existing Worker busy gate remains active until the call exits.
- `CloseScanSession` succeeds only while idle and invalidates the token.
- Connection loss destroys session state. Tokens cannot be transferred to a replacement Worker.
- An operation report must contain release-attempt evidence and no cleanup failure/unknown or device-invalidated state for the session to remain reusable. A native operation error, malformed report, uncertain cleanup, or failed response invalidates it. The existing operation result still reports its actual outcome.
- The existing native process supervision and cancellation mechanisms remain unchanged. A blocked COM driver call cannot be cooperatively interrupted by this contract.

## Profile fix

`DeviceConfigurationProfileFactory.CreateDuplex` now compares parsed, nonempty `Guid` identities instead of UUID text casing. It also verifies both report endpoint IDs and the exact 48 kHz, stereo PCM16 request before producing profile v3. Real Duplex v2 report JSON is preserved unchanged inside the profile. The AudioHost assessment remains `EligibleForRecommendation=false` / `NOT_CERTIFIED`.

## Coordinator hardening

In C3.2 the coordinator remained deterministic-fake-only. C3.3 enables the real mode only through `RealWorkerScanExecutionAdapter` when an already approved Worker token, exact immutable plan, and original discovery snapshot match. Unexpected adapter/coordinator exceptions and unknown cleanup are terminal `UNKNOWN`; accepted prior evidence is retained. It waits for the execution task after requesting bounded cancellation. If execution is still unresolved, the result is `UNKNOWN` and that coordinator instance refuses another run. Unsafe cleanup or stale/lost worker/device identity also latches the instance. Progress subscriber exceptions are isolated from scan state, and the terminal progress event reflects the run outcome.

## Remaining unattended-scan prerequisites after C3.3

1. Add a production GUI consent surface that shows and freezes the entire plan before the operator accepts it; no normal-user flow may construct Worker approval without it.
2. Invoke `RegisterScanSession` only after that approval and retain the pre-registration discovery snapshot.
3. Run operator-reviewed physical QA separately before any real automatic scan is exposed.

`ScanExecutionMode.RealWorker` is available to an explicitly constructed backend adapter, but no GUI entry point exposes it yet. Physical RTL and AudioHost certification remain out of scope.
