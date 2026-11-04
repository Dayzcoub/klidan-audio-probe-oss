# C4.1 Duplex repeatability

C4.1 is a backend-only extension for one exact operator-selected Capture/Render pair. It does not change the C3 one-click scan or profile v3.

## Execution contract

`DuplexRepeatabilityWorkerWorkflow` receives an already refreshed Windows discovery snapshot and verifies both exact active endpoint IDs before preparing the plan. The immutable sequence is:

`MINIMUM → DEFAULT → MINIMUM → DEFAULT → MINIMUM → DEFAULT`

Every operation uses 48 kHz, stereo PCM16, 5 seconds, MMCSS OFF, digital silence on Render, and discarded Capture packets. Each operation gets a new operation ID; the native Worker returns a distinct report ID.

The Worker registration is bounded to:

- exactly two endpoint identities;
- only `ValidateDuplex`;
- one 48 kHz PCM16 format with both periods;
- 600-second session lifetime;
- 5-second maximum operation duration;
- six maximum operations.

The Core authorization is accepted only for `ScanStage.Duplex`, the exact plan fingerprint, six operations or fewer within the global Worker maximum of 24, a maximum authorization duration of 30 seconds per operation, and with Physical RTL and AudioHost activation disabled. One operator confirmation covers the complete immutable series. No second discovery occurs after session registration.

Warnings with verified cleanup continue. Unknown cleanup, device loss, stale identity, timeout, cancellation of an active operation, or unresolved Worker cleanup stops the series and marks remaining runs skipped. Completed observations remain in the result.

### C4.1.1 bounded execution and operator entry

Each awaited Worker operation has a 15-second wall-clock wait bound. A requested cancellation or timeout is not treated as native termination: if termination is not confirmed, that run is `UNKNOWN`, later runs are skipped, and the Worker session is closed with a separate 6-second cleanup bound. A failed or unverified closure leaves the result `UNKNOWN` and the workflow must not reuse the session. Progress subscribers are advisory; an exception from one cannot corrupt execution or terminal state.

The temporary QA entry is available in Developer Tools. It refreshes and verifies the exact Capture/Render pair, prepares the immutable interleaved plan, requests one confirmation, then runs all six operations. The UI shows 30 seconds of active stream time and the separate 600-second Worker session lifetime; it does not expose period buttons or manual save controls. The normal C3 scanner is unchanged.

## Report contract

`DuplexRepeatabilityReportStorage` writes `wasapi-duplex-repeatability`, schema version 1, to a bounded local file using no-overwrite atomic creation and read-back validation. The report retains the exact endpoint IDs, settings, per-run lifecycle/cleanup, operation IDs, native Duplex v2 reports, per-period classifications and provenance. It never writes those observations into profile v3.

For a real Worker run, the workflow saves automatically after the coordinator returns, including safely interrupted partial evidence. The returned result carries the exact `ReportPath` and either `SAVED_AND_RELOADED` or `SAVE_FAILED` plus an error. Unresolved native work is preserved in the report as `UNKNOWN`; it is never converted into fake completed evidence. Synthetic fixture runs are explicitly not saved as real reports.

Classification is independent per period:

- `REPEATABLY_CLEAN`: three usable `DUPLEX_CLEAN` reports;
- `INTERMITTENT_ANOMALIES`: a mixture of clean and other usable statuses;
- `REPEATABLY_ANOMALOUS`: three usable non-clean reports;
- `INSUFFICIENT_EVIDENCE`: fewer than three usable reports or a blocking unknown/cleanup state.

These six bounded five-second observations are diagnostic repeatability evidence only. They do not certify AudioHost or change the existing provisional recommendation rules.

The current native report exposes aggregate counters and interval percentiles. Those values cannot establish Capture/Render event correlation. Precise correlation would require bounded per-event timestamps/sequence data in a future contract; C4.1 does not add unlimited tracing.
