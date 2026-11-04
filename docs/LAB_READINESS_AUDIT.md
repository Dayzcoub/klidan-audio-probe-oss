# L1 — Clean Laboratory Workflow Readiness Audit

Audit basis: branch `feat/s5-a-sustained-device-profiles`, HEAD `967e7a3d78376727a7dd58d6c71e826467dec315`.

Scope is the intended production path:

> Clean Windows machine → exact Capture/Render selection → one automatic scan → Primary/Fallback evaluation → local v4 profile and evidence.

S5-R3 historical cross-scan merging is intentionally excluded. No code, schema, budget, S4 selection, AudioHost or KLIDAN changes were made for this audit. No physical hardware test was run.

## Executive assessment

The implementation is suitable for a controlled laboratory pilot, subject to the explicit hardware-QA gate and the evidence-bundle limitation below. A clean machine does not need historical Scans or Profiles data to prepare or execute the automatic scan. The scan binds to a fresh discovery snapshot, exact endpoint IDs, and one operator approval; the sustained recommendation is recomputed from evidence collected in that same scan.

The strongest remaining product gap is evidence portability. The scan writes a summary, v3/v4 profile and individual persisted reports with read-back validation, and the GUI can open the report folder. It does not yet create one portable archive/manifest containing all source files, SHA-256 hashes, provenance, tool version and operator/session metadata. That is a review/reproducibility blocker for a formal laboratory evidence process, but not a blocker to the scan's local measurement path.

## End-to-end workflow map

```text
Fresh launch
  → Device screen, no Worker connection, no endpoint preselection
  → explicit Worker connection and Windows endpoint discovery
  → exact active Capture/Render selection; physical ambiguity requires explicit pairing
  → Prepare: refresh discovery, bind selected IDs, build immutable plan and budgets
  → explicit consent: no operation starts before acceptance
  → Query → Initialize → short Stream/Duplex screening
  → custom period discovery → short custom Duplex → repeatability
  → sustained screening in OFF and PRO_AUDIO
  → 300 s Standard or 600 s Extended validation for qualified candidates
  → same-scan sustained selection: Primary, independent larger-period Fallback or NONE
  → atomic scan summary/profile/report persistence and read-back
  → GUI result with tested stages, recommendation and explicit NOT_CERTIFIED/NOT_MEASURED limits
```

## Subsystem assessment

| Subsystem | Status | Evidence and finding |
|---|---|---|
| Clean first-run state | PASS | `MainViewModel` starts on Device with no Worker connection or selected endpoint (`tests/gui/KlidanAudioProbe.Gui.Core.Tests/Program.cs:389-395`). Local profile/evidence discovery is refresh-only and empty directories are valid. |
| Endpoint discovery and identity | PASS | `ApplyAutomaticDiscovery` refreshes the Windows snapshot, preserves the selected endpoint IDs, rejects disappeared/inactive IDs and never substitutes by friendly name (`src/gui/KlidanAudioProbe.Gui.Core/ViewModels.cs:1532-1578`). `ScanPlan.ValidateTarget` requires active direction-correct endpoints and distinct IDs (`src/gui/KlidanAudioProbe.Gui.Core/ScanCoordinator.cs:25-56`). |
| Physical Capture/Render pairing | PASS | Automatic scanning requires an operator-selected exact pair for Duplex; ambiguous physical groups require explicit pairing and are not silently merged (`src/gui/KlidanAudioProbe.Gui.Core/ViewModels.cs:1500-1505`, `src/gui/KlidanAudioProbe.Gui.Core/ScanCoordinator.cs:324-348`). |
| Scan preparation and consent | PASS | Prepare performs a fresh endpoint refresh, rebuilds the plan, resets prior consent and displays the immutable plan. Confirm rechecks Worker state, discovery freshness and exact selected IDs before creating the approval identity (`src/gui/KlidanAudioProbe.Gui.Core/ViewModels.cs:1498-1529`, `1592-1623`). |
| Format query and Initialize | PASS | The real adapter sends only the approved endpoint scope and bounded 48 kHz PCM16 candidates; Initialize evidence remains separate from Start/stream evidence (`src/gui/KlidanAudioProbe.Gui.Worker/RealWorkerScanExecutionAdapter.cs:40-128`, `src/gui/KlidanAudioProbe.Gui.Core/ScanCoordinator.cs:300-322`). |
| Period discovery | PASS | Exact Capture/Render period discovery is a separate bounded stage. Candidate inventory retains period, `Period100ns`, sample format, endpoint, state, provenance and MMCSS fields (`src/gui/KlidanAudioProbe.Gui.Core/AutomaticScanPlan.cs:37-75`). |
| Runtime-tested candidate inventory | PASS | `WithRuntimeResults` promotes candidates only when runtime reports match endpoint, direction, period, format, MMCSS, provenance and expected geometry; custom periods additionally require independently accepted period-discovery geometry (`src/gui/KlidanAudioProbe.Gui.Core/AutomaticScanPlan.cs:105-168`). |
| MMCSS OFF / PRO_AUDIO | PASS | Sustained screening explicitly runs both `OFF` and `PRO_AUDIO` for each shortlisted candidate, then long validation preserves the selected mode in the report and evidence (`src/gui/KlidanAudioProbe.Gui.Worker/WorkerAutomaticScanWorkflow.cs:354-401`). The offline R2.1 fix correctly treats inventory geometry as MMCSS-independent while retaining strict report/child-operation MMCSS validation (`src/gui/KlidanAudioProbe.Gui.Core/OfflineSustainedProfileRecovery.cs:112-164`). |
| Short-run screening and repeatability | PASS | The workflow performs bounded short custom Duplex and repeatability before sustained validation; UNKNOWN, failed cleanup or unsafe identity stops later work (`src/gui/KlidanAudioProbe.Gui.Worker/WorkerAutomaticScanWorkflow.cs:230-342`, `CanContinueAfterSustained` at `:529-542`). |
| Sustained validation | PASS | Qualified candidates are screened for 60 seconds in both MMCSS modes, then up to two ordered long validations run at the selected Standard/Extended duration. Each report is saved atomically and read back (`src/gui/KlidanAudioProbe.Gui.Worker/WorkerAutomaticScanWorkflow.cs:354-424`, `src/gui/KlidanAudioProbe.Gui.Worker/SustainedDuplexWorkerWorkflow.cs:10-65,69-103`). |
| Budgets and safety bounds | PASS | Preparation computes operation, active-time and wall-clock bounds; Worker authorization caps operations and duration, and later orchestration checks its own total budget before each stage (`src/gui/KlidanAudioProbe.Gui.Worker/WorkerAutomaticScanWorkflow.cs:83-112,145-180,230-312`). |
| Primary/Fallback selection | PASS | `SustainedDuplexSelection.Recommend` groups by exact endpoint pair, format, period and MMCSS, requires clean persisted 300-second-or-longer evidence, selects the lowest exact buffer as Preferred and only an independently clean larger buffer as Fallback; otherwise Fallback is null/NONE (`src/gui/KlidanAudioProbe.Gui.Core/SustainedDuplexValidation.cs:90-158`). |
| Exact evidence and geometry | PASS | Sustained resolver checks derived operation-ID filenames, envelope identity, report identity, endpoint/direction, requested settings, actual buffer frames, duration, cleanup and counters (`src/gui/KlidanAudioProbe.Gui.Core/SustainedDuplexEvidenceResolver.cs:16-69`). v4 factory repeats the report, MMCSS, identity and independent geometry checks (`src/gui/KlidanAudioProbe.Gui.Core/DeviceConfigurationProfile.cs:291-330`). |
| v4 persistence | PASS | Real sustained results are passed to `CreateSustained`; the pair profile is saved atomically, parsed again, and the scan summary records stage state and profile path (`src/gui/KlidanAudioProbe.Gui.Worker/WorkerAutomaticScanWorkflow.cs:418-440`, `src/gui/KlidanAudioProbe.Gui.Core/DeviceConfigurationScanStorage.cs:94-190`). |
| Error recovery and UNKNOWN outcomes | PASS | Timeout, cancellation, worker loss, unsafe cleanup and identity loss become `INCOMPLETE`, `CANCELLED`, `FAILED` or `UNKNOWN`; uncertain Worker state prevents unsafe reuse and does not fabricate evidence (`src/gui/KlidanAudioProbe.Gui.Core/ScanCoordinator.cs:160-285`, `src/gui/KlidanAudioProbe.Gui.Worker/WorkerAutomaticScanWorkflow.cs:450-474`). |
| Operator GUI | PARTIAL | The GUI displays the immutable plan, stage progress, selected recommendation, persistence result, operation diagnostics, `AudioHost: NOT_CERTIFIED` and physical RTL limitations (`src/gui/KlidanAudioProbe.Gui.App/MainWindow.xaml:180-204`). The normal result view is clear for operators, but detailed per-candidate/report inspection remains under technical details rather than a dedicated laboratory report view. |
| Evidence storage and review | PARTIAL | Summary, profiles and individual reports are bounded, atomic/read-back validated and discoverable locally. The GUI exposes “Открыть папку отчёта” (`src/gui/KlidanAudioProbe.Gui.App/MainWindow.xaml:195-203`). There is no one-click evidence bundle, manifest or integrity file tying every report/profile/summary byte to one review package. |
| Sanitized portability | PARTIAL | `DeviceConfigurationProfileJson.SerializeSanitized` produces a redacted portable profile (`src/gui/KlidanAudioProbe.Gui.Core/DeviceConfigurationProfile.cs:350-379`), and the Evidence Library can inspect local records (`src/gui/KlidanAudioProbe.Gui.Core/EvidenceLibrary.cs:61-212`). Sustained source reports and a complete scan bundle do not have an equivalent operator-facing sanitized export workflow. |
| Historical-data dependency | PASS | Automatic scan preparation and execution use the fresh discovery/Worker plan and current selected endpoints. Historical recovery is a separate opt-in workflow and is not called by the automatic scanner (`src/gui/KlidanAudioProbe.Gui.Core/ViewModels.cs:1498-1630`, `src/gui/KlidanAudioProbe.Gui.Core/OfflineSustainedProfileRecovery.cs:29-80`). |

## Answers to the key questions

1. Yes in implementation terms: a fresh machine can execute the Real Worker automatic workflow and persist a v4 when exact pair, sustained capability and all evidence gates succeed. This is covered by deterministic Worker/GUI tests, not physical hardware QA.

2. Yes. Preferred and Fallback are selected from the same scan's persisted sustained evidence; Fallback requires its own clean long run at a larger independently observed buffer geometry.

3. Yes. A single qualifying configuration produces `Fallback = NONE`; the selection and v4 schema preserve null fallback rather than inventing one.

4. Yes for accepted evidence. Exact endpoint IDs, requested MMCSS, operation/report GUIDs, derived report paths, independent geometry, duration, cleanup, provenance and quality counters are checked before selection and again before v4 creation.

5. Mostly. The GUI clearly shows stages, result, recommendation and unverified AudioHost/physical RTL state. A lab reviewer still needs the local evidence files or a future bundle to inspect every candidate/report without opening technical files manually.

6. No historical dependency was found in the automatic path. Empty Scans/Profiles directories are valid first-run state; the required state is the current Worker, fresh discovery and explicit pair selection.

7. Not fully. The local files are sufficient for a same-machine review, but there is no complete portable bundle with a manifest and hashes. This is the principal evidence-process gap.

8. Consistency requires fixed Windows version/build, one exact active pair, explicit endpoint identity capture, the selected sustained mode, stable power/device conditions, complete local output retention, and recording the scan/profile/session IDs with the tool build. The current code records most machine-readable identities, but the operator procedure must preserve the surrounding lab metadata.

9. Standard runs 300-second long validation; Extended runs 600-second long validation. Extended increases active time and wall-clock exposure, improves duration coverage, and may increase device/driver failure opportunity. Neither mode measures physical RTL, certifies AudioHost, or replaces repeated laboratory sessions.

10. Genuine blockers are incomplete/UNKNOWN cleanup or Worker state, missing exact pair identity, missing sustained capability, no qualifying clean sustained evidence, failed persistence/read-back, and the absence of a portable evidence bundle for formal review. Optional improvements are richer candidate visualization, automated lab metadata capture and a sanitized bundle exporter. Physical QA remains an exit criterion, not a code-path failure.

## Critical blockers ranked by severity

### P0 — Formal evidence portability is incomplete

The application does not produce a single evidence package with a manifest, SHA-256 hashes, tool/build identity and explicit membership of the scan summary, v3 base profile, v4 profile and every source report. Opening the local folder is useful but does not provide a review-safe chain of custody or a reproducible transfer unit.

Impact: local laboratory operation can proceed; formal cross-machine review, archival and comparison are manual and error-prone.

### P1 — Physical laboratory validation is still outstanding

Automated tests use deterministic Worker/native fixtures. They cannot prove the real device's driver behavior, sustained stability, endpoint persistence, physical routing, analog loopback or actual RTL. The product correctly leaves Physical RTL as `NOT_MEASURED` and AudioHost as `NOT_CERTIFIED`.

Impact: the scanner cannot be declared production-lab-ready solely from CI/test results.

### P1 — Operator-visible detail is adequate but not yet a complete lab report

The normal result page summarizes stages and recommendation, while the detailed operation ledger is collapsed and the operator must open local files for full report-level review. This is not a measurement-integrity failure, but it increases review time and makes multi-session laboratory consistency dependent on manual procedure.

## Minimal implementation plan

1. Add an explicit, opt-in “Export evidence bundle” action after a completed real scan.
2. Write a new bundle directory or archive atomically containing the scan summary, v3 base profile, v4 profile, all derived sustained reports and relevant period/repeatability reports.
3. Add a versioned manifest with SHA-256, file role, schema/version, session/profile/report identities, endpoint redaction policy, tool build and export timestamp. Validate the bundle by read-back before reporting success.
4. Provide sanitized export rules for every report type; keep local full-identity evidence separate from portable redacted evidence.
5. Add an operator-facing compact candidate/evidence table to the result view. Do not change selection, budgets or profile schemas.

## Clean-machine operator QA procedure

1. Start on a Windows 10/11 x64 laboratory machine with the device and approved driver installed. Confirm no prior KLIDAN Audio Probe Scans/Profiles are needed; do not import historical scans.
2. Launch the Probe. Confirm the initial screen is Device, Worker is not silently connected, no endpoint is preselected and no audio operation has started.
3. Connect the bundled Worker through the explicit GUI action and refresh Windows endpoints.
4. Select the exact active Capture and Render endpoints. If the physical grouping is ambiguous, choose both directions explicitly. Do not resolve by friendly name alone.
5. Review the automatic plan: exact IDs/pair, 48 kHz PCM16 scope, period/custom scope, OFF and PRO_AUDIO sustained coverage, Standard or Extended duration, operation/active/wall-clock limits, no raw PCM persistence, no physical RTL and no AudioHost certification.
6. Give the single explicit scan consent. If discovery, Worker state or endpoint identity changes, cancel and prepare a new plan.
7. Allow the scan to complete. Treat any UNKNOWN, failed cleanup, stale endpoint, missing report or save/read-back failure as a failed session; retain its incomplete evidence and do not retry blindly on a possibly active Worker.
8. On the result page, record device name, scan session, selected Primary, Fallback or NONE, confidence, sustained duration, MMCSS, AudioHost and physical RTL limits.
9. Preserve the complete local Scans, Profiles and SustainedDuplex output for the session. Until a bundle exporter exists, copy the exact files as one operator-controlled set and record their hashes externally.
10. Repeat on a clean session/device condition when the laboratory protocol requires repeatability. Never merge historical scans into a new authoritative profile.

## Exit criteria for laboratory-ready declaration

- One real-device run completes on a clean Windows machine with an explicitly selected exact pair.
- The scan summary is `COMPLETED`; every selected Primary/Fallback report is real, persisted, read-back validated, independently geometric, MMCSS-consistent and safely cleaned.
- The v4 profile is read back with exact source session/report identities; Fallback is `NONE` when no independent larger clean result exists.
- No Worker, endpoint, cleanup, persistence or evidence-integrity outcome is UNKNOWN.
- Operator-visible result and retained files clearly state AudioHost `NOT_CERTIFIED` and Physical RTL `NOT_MEASURED` unless separately measured by an authorized workflow.
- The complete evidence bundle can be transferred and independently verified by manifest/hash, or the lab formally accepts the interim manual-copy procedure.
- At least one additional laboratory session reproduces the documented procedure without relying on historical Scans/Profiles state.
- Physical QA signs off the device-specific interpretation; automated tests alone do not satisfy this criterion.

## Validation performed for this audit

This was a read-only source audit. No physical hardware operations, historical recovery, Worker run, WASAPI operation, schema change, budget change or product code change was performed. Existing automated validation was used as implementation evidence; the audit does not claim physical QA evidence.
