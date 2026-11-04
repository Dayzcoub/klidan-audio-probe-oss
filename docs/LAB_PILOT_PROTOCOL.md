# KLIDAN Audio Probe laboratory pilot protocol

This protocol applies to the frozen L3-A `win-x64` distribution. It is an operator procedure, not a certification claim. Physical RTL and AudioHost certification are explicitly out of scope.

## Before the scan

1. Use a dedicated clean Windows 10/11 x64 user account and machine. Do not copy source code, development tools, historical Profiles, Scans, or prior operator evidence into the account.
2. Install only the approved device driver and confirm the device is visible in Windows. Record Windows build, driver version, device firmware, distribution package SHA-256, source SHA, and the displayed build timestamp.
3. Unpack the package to a new local directory and run `Launch-LabDistribution.ps1`. It verifies the distribution and hashes, requires Windows x64, refuses an already-running Audio Probe process, and does not connect the Worker or start audio.
4. In the Device screen, trigger Windows discovery deliberately. Select the exact Capture and Render endpoints. Never choose by friendly name alone; do not allow an automatic substitute after unplug/rebind.
5. Select `Standard (300 s)`. Review the bounded plan, exact endpoints, periods, MMCSS modes and stop conditions. Give the one explicit consent only after confirming that Capture packets are discarded, Render uses digital silence, no PCM is saved, and no physical RTL or AudioHost certification is attempted.

## During the scan

The automatic scan is one operator-approved bounded run. Stop and record the visible state if the endpoint disappears, Worker cleanup becomes UNKNOWN, the device is busy, the wrong endpoint is shown, or the UI reports an unresolved outcome. Do not restart the Worker to recover an UNKNOWN result and do not repeat physical testing without a new operator decision.

## Interpreting the result

- `COMPLETED` with a saved/read-back v4 profile is a complete software evidence result for the exact Capture/Render pair.
- `INCOMPLETE`, `FAILED`, `CANCELLED`, or `UNKNOWN` is not a valid profile result. Preserve the summary and technical details; do not promote it manually.
- Read the v4 profile back and record Primary and Fallback exactly. `Fallback = NONE` is valid when only Primary independently qualifies.
- `AudioHost = NOT_CERTIFIED` and `Physical RTL = NOT_MEASURED` must remain visible. Neither is implied by a clean sustained run.

## Evidence retention

Before closing the GUI, use `Экспортировать результаты`, review the `LOCAL_FULL_EVIDENCE` warning, choose a new destination, and confirm explicitly. The export must report that the ZIP was created and independently verified. Preserve the ZIP and its external SHA-256 manifest with the session notes. Verify the ZIP again on a separate offline review machine or with the Core verifier before transfer.

Retain the package SHA-256, source SHA, distribution manifest, scan bundle, v4 profile identity, original measurement timestamp, export timestamp, Windows build, driver/firmware metadata, exact endpoint selections, scan mode, visible lifecycle, Primary/Fallback, and all failure/UNKNOWN details. Do not publish the bundle: it is full local evidence and may contain endpoint identifiers and machine-specific data.

## PASS/FAIL

PASS requires: exact pair selected; Standard 300-second plan consented; scan lifecycle `COMPLETED`; profile and reports saved/read back; Primary/Fallback match the v4 profile; `NOT_CERTIFIED` and `NOT_MEASURED` remain explicit; bundle export and independent verification succeed; original local evidence remains unchanged.

FAIL requires retention of the evidence and operator notes when any lifecycle is UNKNOWN/INCOMPLETE/FAILED/CANCELLED, a report is missing or corrupt, endpoint identity changes, profile readback fails, bundle verification fails, or the application cannot prove the source/build identity.

No result from this protocol certifies acoustic routing, physical RTL, AudioHost, KLIDAN integration, or suitability for unattended production use.
