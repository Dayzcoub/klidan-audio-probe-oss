# L10N-5 Final Release Candidate QA

Date: 2026-11-05
Repository: `Dayzcoub/klidan-audio-probe-oss`
Branch: `feat/l10n-release-hardening`
Source HEAD tested: `7646ec53b3ffc3d3d1a4c202925b72b7edb75dde`
Version: `0.1.0-preview.1`

## Scope and safety

Fresh validation was run against the source HEAD above. Native tests were
synthetic and did not run physical audio tests. The real GUI was launched and
inspected in both locales; passive endpoint discovery was explicitly requested,
but no audio test or scan was started. The GUI and Worker exited after a normal
window close. No global DPI, device, driver, or Windows audio setting was
changed.

## Fresh validation evidence

| Check | Result | Evidence |
|---|---|---|
| Native Release build | PASS | `cmake --build build --config Release`, exit 0 |
| Native CTest | PASS | `ctest --test-dir build -C Release --output-on-failure`: 119/119 passed, 20.35 s |
| GUI Core/WPF | PASS | Release run: `All GUI Core checks passed (804)` |
| Worker IPC/process | PASS | Release run: `All worker IPC/process checks passed (289)` |
| GUI Release | PASS | Release build completed with 0 warnings and 0 errors |
| Resource parity | PASS | Both RESX files have 1,216 keys, zero empty values, zero key differences, zero placeholder mismatches |
| JSON/evidence compatibility | PASS | Included in the fresh 804 GUI Core/WPF checks; no schema or IPC production code changed in this QA |
| Synthetic/production separation | PASS | Core checks cover synthetic provenance boundaries; real and synthetic UI areas remained distinct |
| Developer Tools | PASS | Normal launch omitted Developer Tools navigation; `--developer-tools` launch exposed its synthetic controls |
| Language persistence | PASS | English selection survived restart; Russian was also visually inspected |
| English/Russian installer language | PASS | Inno Setup compiler accepted both language definitions; isolated `/LANG=english` and `/LANG=russian` installs and uninstalls returned exit 0 |
| Installer source binding | PASS | Manifest records `feat/l10n-release-hardening@7646ec53b3ffc3d3d1a4c202925b72b7edb75dde` |
| Self-contained runtime | PASS | Fresh package script published `win-x64` self-contained GUI; manifest records `selfContained: true` |
| Native dependency audit | PASS | Manifest lists only Windows system DLLs; no unsupported external MSVC runtime imports |
| License bundle | PASS | Manifest contains five hashed license/notice files: project LICENSE and THIRD_PARTY_NOTICES plus three .NET runtime notices/licenses |
| Installer publisher | PASS | Inno source sets `AppPublisherURL=https://github.com/Dayzcoub/klidan-audio-probe-oss` |
| Isolated install/uninstall | PASS | Each locale installed 258 files under a unique temporary directory, found `unins000.exe`, and removed the temporary install with exit 0; existing user profile/evidence directories remained present |
| GUI close / Worker termination | PASS | Closing the real GUI left no GUI or Worker process |
| `git diff --check` | PASS | Clean after report/metadata edits |

### Installer artifact

Path: `out/installer/KLIDAN-AudioProbe-Setup.exe`
Manifest: `out/installer/KLIDAN-AudioProbe-Setup.exe.manifest.json`
Size: 47,679,706 bytes
SHA-256: `07694b8911c7079717441c91820b07cf1b5ec428f143a30880e85ea4fcd5f2d6`
Unsigned: yes; SmartScreen/Defender may show a warning.

The installer contains Developer Tools only as the explicit command-line gated
feature; the standard launch does not enable them. This was confirmed on the
Release GUI in its normal and `--developer-tools` launches. The installed app
itself was not launched during the isolated install check.

## Interactive GUI and DPI

The Release GUI was inspected at the available desktop size (captured area
1428×896). The current window DPI was queried using `GetDpiForWindow`: 96 DPI
(100%). Both language variants displayed legible primary device screens with no
obvious overlap or clipping. English was active after restart; the Russian
variant was also inspected. No automatic scan, physical stream, or active test
was started.

The explicit passive “Find / refresh devices” action remained in “device list
is being refreshed” for over two minutes in the GUI. The Worker process stayed
alive with negligible CPU use, and no endpoint list appeared; the same native
build’s passive `KlidanAudioProbe.exe --list` exited 0 and produced 89 output
lines. The cause was not isolated in this QA, so this is a GUI discovery
blocker. Consequently the full requested navigation, selection, reports,
recommendations, history, tooltip/accessibility, and Automatic Scan screen
review could not be completed in an operational device context. GUI tests cover
their corresponding layouts and command gating, but do not substitute for this
live interaction.

| DPI | Result | Reason |
|---|---|---|
| 100% | NOT_RUN | Current display reported 96 DPI, but the exact requested 1440×900 and minimum-window cases were not both established; available capture was 1428×896. |
| 125% | NOT_RUN | No isolated Windows VM/test profile was available; global desktop DPI was left unchanged. |
| 150% | NOT_RUN | No isolated Windows VM/test profile was available; global desktop DPI was left unchanged. |

## Release metadata

README, BUILDING, SECURITY, THIRD_PARTY_NOTICES and hardware compatibility
documentation were reviewed. README and hardware compatibility now explicitly
state the preview/unsigned-installer, no physical RTL certification,
`AudioHost NOT_CERTIFIED`, and physical audio QA not performed in this pass.
`RELEASE_NOTES_v0.1.0-preview.1.md` contains the public preview disclaimer.
No GitHub Release or tag was created.

The matrix’s device rows are historical operator summaries and are not evidence
that physical QA ran against this source HEAD. No physical audio QA was
performed in this pass.

## Acceptance

```text
NATIVE_CTEST = PASS
GUI_CORE_WPF = PASS
WORKER_TESTS = PASS
RESOURCE_PARITY = PASS
RU_VISUAL_QA = FAIL (primary screen inspected; requested full workflow and DPI matrix incomplete)
EN_VISUAL_QA = FAIL (primary screen inspected; requested full workflow and DPI matrix incomplete)
DPI_100 = NOT_RUN
DPI_125 = NOT_RUN
DPI_150 = NOT_RUN
DEV_TOOLS = PASS
JSON_IPC_COMPATIBILITY = PASS
INSTALLER_BUILD = PASS
INSTALL_UNINSTALL = PASS
LICENSE_BUNDLE = PASS
PR_DESCRIPTION = UPDATED
FINAL_HEAD = pending report commit
INSTALLER_SHA256 = 07694b8911c7079717441c91820b07cf1b5ec428f143a30880e85ea4fcd5f2d6
READY_FOR_MERGE = NO
READY_FOR_PUBLIC_RELEASE = NO
```

Remaining blockers are the live GUI endpoint discovery hang, incomplete full
workflow visual inspection, and missing exact-size 100% plus isolated 125% and
150% visual runs. Physical QA is explicitly outside this pass.

## QA-6 endpoint discovery investigation — 2026-11-05

This is a local investigation against starting HEAD
`0bffd09e81a5902db5448b268be68f0ec6196723`; the changes below are not committed
or pushed. The first proposed diagnosis (a stale saved-profile status label)
was incomplete. In the latest Release GUI, opening Developer Tools and pressing
Find / refresh left the Worker disconnected and showed no endpoint rows. A
successful status was observed in an earlier session, but could not be
reproduced consistently in this run. Therefore the root cause is unresolved and
the GUI discovery fix is not accepted.

Fresh checks during this investigation:

| Check | Result | Evidence |
|---|---|---|
| Native Release build | PASS | `cmake --build build --config Release --parallel`, exit 0 |
| Native CTest | PASS | 119/119, 18.07 s |
| GUI Release build | PASS | `dotnet build ...Gui.App.csproj -c Release`, 0 warnings/errors |
| GUI Core/WPF | FAIL | Fresh run repeatedly stops at pre-existing assertion `Period discovery entry is visible with an explicit unavailable reason` (test expects an unavailable reason although its injected workflow makes the state Ready). |
| Worker IPC/process | PASS | 292 checks, including 5-second discovery timeout, late response and later independent correlation |
| Resource parity | PASS | 1,219 keys in each locale, zero missing/empty values, zero placeholder mismatches |
| GUI endpoint discovery | FAIL | Release GUI on real Windows: refresh left Worker disconnected and endpoint list empty; prior refresh durations around 9–11 seconds are not accepted as proof because the latest result contradicted them. |
| Shutdown cleanup | PASS | Closed GUI; subsequent process query found no GUI or Worker process. |

The app UI trace listener experiment did not produce a trace file. No endpoint
IDs or user paths were included in the attempted trace messages. Detailed
stage-by-stage request/response timing is therefore unavailable. No active
audio operation was started. No commit or push was made because live discovery
and GUI Core/WPF validation did not pass.

```text
ROOT_CAUSE = unresolved
DISCOVERY_GUI = FAIL
DISCOVERY_DURATION_MS = inconsistent; prior ~9,000–11,000 ms observations not accepted as PASS
IPC_TIMEOUT_HANDLING = PASS (Worker IPC regression checks)
LOADING_CLEANUP = FAIL (GUI returned disconnected/empty state)
STALE_ENDPOINT_SAFETY = PASS (failed/disconnected result exposes no current rows)
REPEAT_REFRESH = NOT_CONFIRMED
RU_GUI = NOT_RUN in latest build
EN_GUI = FAIL
GUI_CLOSE_WORKER_CLEANUP = PASS
NATIVE_CTEST = PASS (119/119)
GUI_TESTS = FAIL (pre-existing assertion above)
FINAL_HEAD = 0bffd09e81a5902db5448b268be68f0ec6196723 + uncommitted local changes
WORKTREE = DIRTY
READY_FOR_DPI_QA = NO
```

## QA-6A endpoint discovery root cause — 2026-11-05 (supersedes the QA-6 finding above)

The earlier report's claim that a Release refresh left the Worker disconnected
was not supported by a captured trace and is superseded by this controlled
diagnostic run. QA logging was enabled explicitly with
`KLIDAN_ENDPOINT_DISCOVERY_QA_LOG` and began before Worker startup. The local
trace is:

`C:\Users\Jizzo\AppData\Local\Temp\klidan-endpoint-qa-bc17249a9b02426e85d0e2a461ae1555.log`

The actual Native Worker IPC discovery path completed successfully twice in
one GUI launch. The first request was sent at elapsed 55,066 ms and its
correlated response arrived at 55,089 ms (23 ms); the second was sent at
164,234 ms and its correlated response arrived at 164,255 ms (21 ms). Both
results reached the UI, and Loading exited 1 ms later. Worker state progressed
Disconnected → Starting → Connecting → Handshaking → ConnectedFake. Process
exit code 259 while connected is STILL_ACTIVE; normal shutdown later recorded
exit code 0. The GUI and Worker processes were absent after closing the GUI.
Developer Tools showed 87 real Windows endpoint rows. No endpoint identifiers,
payloads or personal paths were written to the trace.

The apparent persistent “Loading” symptom is explained by the saved-profile
operator status being set to a “device list is refreshing” message at discovery
start, but not being given a terminal status on success/failure. The actual
`WindowsDiscoveryLoading` flag was cleared after each response. The current
ViewModel change sets a completed status that explicitly keeps profile binding
unverified, and sets a failure status on errors. Thus this run identified a
stale status presentation issue; it did **not** reproduce a 120-second GUI
Loading state, Worker exit, IPC timeout, or pipe failure. Standalone native
`--list` was not used as a substitute for the GUI IPC trace.

The initial trace also revealed an AsyncLocal correlation leak into the
long-lived IPC reader task. Reader/exit-monitor tasks now start with execution
context flow suppressed; request and response retain their explicit IPC
correlation IDs. This is diagnostic correlation correctness, not an audio or
transport behavior change.

Validation on this working tree:

| Check | Result | Evidence |
|---|---|---|
| GUI Core/WPF | PASS | `dotnet run --project tests/gui/KlidanAudioProbe.Gui.Core.Tests/KlidanAudioProbe.Gui.Core.Tests.csproj -c Release`; all 810 checks passed |
| Worker IPC/process | PASS | `dotnet run --project tests/gui/KlidanAudioProbe.Gui.Worker.Tests/KlidanAudioProbe.Gui.Worker.Tests.csproj -c Release`; all 292 checks passed, including timeout, cancellation, late response, disconnect and shutdown cases |
| GUI Release | PASS | `dotnet build src/gui/KlidanAudioProbe.Gui.App/KlidanAudioProbe.Gui.App.csproj -c Release`; 0 warnings, 0 errors |
| Diff whitespace | PASS | `git diff --check`, exit 0 |
| Interactive discovery | PARTIAL | Two real GUI refreshes passed in en-US (23 ms and 21 ms IPC response); the 3-run RU/EN matrix was stopped by Computer Use after the physical Escape key event |

Several Core test expectations in the existing suite hard-coded Russian UI
phrases while the suite selects en-US. These expectations now use the active
resource strings; the machine status, endpoint and command assertions remain.
The gated-discovery test now pumps the WPF Dispatcher instead of blocking it
with `SpinWait` while awaiting a UI continuation.

No active audio operation was run. This verified code/test checkpoint is being
committed and pushed to the existing branch. The remaining interactive RU/EN
three-refresh check is still required before DPI QA.

```text
ROOT_CAUSE = IDENTIFIED (stale saved-profile status made completed discovery look ongoing; no IPC hang reproduced)
LAST_SUCCESSFUL_STAGE = ui-update-complete
FIRST_FAILED_STAGE = NONE in the two traced discovery operations
WORKER_EXIT_REASON = normal GUI shutdown, exitCode=0; exitCode=259 while connected means STILL_ACTIVE
IPC_RESULT = PASS, correlated responses at 23 ms and 21 ms
LOADING_CLEANUP = PASS (Loading=false and cleanup-complete recorded for both)
DISCOVERY_3_RUNS = NOT_RUN (only two refreshes occurred in the one GUI launch)
PERIOD_DISCOVERY_TEST = PASS (included in 810 GUI Core/WPF checks)
GUI_TESTS = PASS (810 Core/WPF; 292 Worker IPC/process)
FINAL_HEAD = 13eef3767d4e7ab71d2db256ae5643ee828e1912
WORKTREE = clean after checkpoint push
READY_FOR_DPI_QA = NO (RU/EN three-refresh interactive validation remains incomplete)
```

## QA-6B discovery verification — 2026-11-05

Validated the current branch tip `c3af6b241ad3bc880c4bc4df477aa1f1cdee618f`
through Windows UI Automation. QA trace files were created in the local Temp
directory and contain state/correlation timing only:

- en-US: `C:\Users\Jizzo\AppData\Local\Temp\klidan-qa6b-e4bc6fb0eee448659d2f624a76915f64.log`
- ru-RU: `C:\Users\Jizzo\AppData\Local\Temp\klidan-qa6b-ru-2c87a9d41cd148309e58001e4c341037.log`
- isolated install: `C:\Users\Jizzo\AppData\Local\Temp\klidan-qa6b-installed-51128808da6b4b15bd2d9231b9f672ff.log`

Each trace recorded three `loading-enter` → `gui-cleanup-complete` pairs,
three request/response correlation matches, no timeout/transport/Worker errors,
and Loading false at cleanup. Measured Loading-to-cleanup durations:

| Locale | Run 1 | Run 2 | Run 3 | Groups after refresh |
|---|---:|---:|---:|---:|
| en-US | 232 ms | 50 ms | 60 ms | 35 |
| ru-RU | 192 ms | 51 ms | 64 ms | 35 |

The English session retained its selected physical group across the repeated
refreshes. In the Russian session, a physical group was selected after the
first refresh and remained selected through runs 2 and 3. The saved-profile
status showed the localized completed message stating that the saved binding
remains unverified; it did not remain in a refreshing/loading state. Windows
reported nine present AudioEndpoint devices, while the GUI exposed 35 physical
device groups. No active audio action was invoked.

The GUI language selection persisted `ru-RU` across process restart; the newly
started GUI title and controls were Russian. The original `en-US` preference
was restored after verification so the operator's starting setting was
preserved.

For installed-app QA, a fresh self-contained installer was built from the
current HEAD using `scripts/Build-LabInstaller.ps1`; the build manifest records
the exact branch and source SHA. The new installer was installed to an isolated
Temp directory, launched normally without `--developer-tools`, completed one
Russian passive refresh (35 groups; 234 ms Loading-to-cleanup), and closed with
its Worker. The GUI and Worker processes exited; the isolated install was then
uninstalled. The installer is unsigned.

Installer: `C:\Users\Jizzo\AppData\Local\Temp\klidan-qa6b-installer-db934ce2fda94fa9876ed526e8c7dec8\KLIDAN-AudioProbe-Setup.exe`

SHA-256: `e4f9376ccaccc71dbd0ef2f840a3e60eb00de125857539ccdbf2677757d638ff`

The first packaging attempt exposed a helper-script limitation when passing a
custom installer basename; Inno Setup has a fixed `OutputBaseFilename`. The
build was repeated in a new empty directory using the configured basename and
completed successfully. No repository code or version was changed.

Fresh test runs:

| Check | Result | Evidence |
|---|---|---|
| GUI Core/WPF | PASS | 810 checks; `dotnet run --project tests/gui/KlidanAudioProbe.Gui.Core.Tests/KlidanAudioProbe.Gui.Core.Tests.csproj -c Release` |
| Worker IPC/process | PASS | 292 checks; `dotnet run --project tests/gui/KlidanAudioProbe.Gui.Worker.Tests/KlidanAudioProbe.Gui.Worker.Tests.csproj -c Release` |
| Installed Release GUI | PASS | Current-HEAD installer; normal Russian launch, no Developer Tools UI, passive discovery, clean close/uninstall |
| Worker stability | PASS | Six GUI refreshes plus installed-app refresh had correlated responses, no Worker error transitions, clean shutdown |

The saved language setting was temporarily changed to ru-RU for the restart
check and then restored to the original en-US value. No production source or
test code changed during QA-6B; only this report is modified locally.

```text
EN_REFRESH_3_3 = PASS
RU_REFRESH_3_3 = PASS
REFRESH_DURATIONS_MS = en-US [232, 50, 60]; ru-RU [192, 51, 64]
LOADING_CLEANUP = PASS
WORKER_STABILITY = PASS
INSTALLED_GUI = PASS
GUI_TESTS = PASS (810)
WORKER_TESTS = PASS (292)
FINAL_HEAD = c3af6b241ad3bc880c4bc4df477aa1f1cdee618f
READY_FOR_DPI_QA = YES
```

## QA-7 DPI and visual regression — 2026-11-05

QA-7 began from the QA-6B report checkpoint `5b48272b307e8340b1f0515ac04a93199a27d42c`. The Windows system and app window both reported 96 DPI (`GetDpiForSystem` / `GetDpiForWindow`) on a 3440×1440 primary display. No isolated VM or display session was available. The operator desktop scaling was left unchanged; 120 DPI (125%) and 144 DPI (150%) were not tested.

The current Release GUI was started and inspected through Windows UI Automation. The `en-US` launch exposed English controls and accessibility names. Changing the selector to `ru-RU` and restarting produced Russian controls and accessibility names. Screenshots were captured outside the repository in `%LOCALAPPDATA%\Temp`; other desktop windows overlay the right/lower portion of the app, so screenshot-based clipping/overlap review is incomplete. Actual full-window 1440×900 and minimum 1024×720 inspections were not completed in this session. The earlier QA-6B report records six passive discovery refreshes (three per locale); QA-7 did not repeat that matrix. One passive refresh per language was not completed in this session.

Three localization defects discovered in QA-7 were corrected without changing machine values: Russian Windows sidebar presentation, the Russian Windows Inspector title, and English display labels for synthetic demo endpoints. Demo endpoint raw `Name`/`State`/`Format`, session endpoint names, and JSON shape remain unchanged; localized computed properties are excluded from serialization. A further English UI leak from the default disconnected Worker message was fixed at the presentation layer only. The original `WorkerState.Message` and non-default diagnostics remain unchanged. Automated tests cover both locales and raw-data preservation.

Fresh checks on the final source changes:

| Check | Result | Evidence |
|---|---|---|
| Resource parity | PASS | 1223 keys per locale; 0 missing, extra, or empty RU values; 0 placeholder mismatches |
| GUI Core/WPF | PASS | 819 checks via `dotnet run --project tests/gui/KlidanAudioProbe.Gui.Core.Tests/KlidanAudioProbe.Gui.Core.Tests.csproj -c Release`; existing nullable warning CS8602 at Program.cs:1936 |
| GUI Release | PASS | `dotnet build src/gui/KlidanAudioProbe.Gui.App/KlidanAudioProbe.Gui.App.csproj -c Release --no-restore`; 0 warnings, 0 errors |
| Worker IPC/process | NOT_RUN | No Worker IPC or protocol implementation changed |
| Native CTest | NOT_RUN | No native or transport code changed |
| Diff whitespace | PASS | `git diff --check` |

The Results page was not entered because it requires completed measurement evidence; no active audio operation was run. Discovery timings and Worker stability remain as recorded in QA-6B, not re-measured here. Developer Tools ON and full device/format/result navigation were not revalidated on the final build in this session. Visual QA is therefore partial despite the verified 96-DPI locale startup checks.

```text
DPI_100 = PASS (96 DPI API readings; visual matrix partial)
DPI_125 = NOT_RUN (no isolated DPI environment; system settings left unchanged)
DPI_150 = NOT_RUN (no isolated DPI environment; system settings left unchanged)
RU_VISUAL = PASS (startup/accessibility text only; full matrix incomplete)
EN_VISUAL = PASS (startup/accessibility text only; full matrix incomplete)
MIN_WINDOW = NOT_RUN
NORMAL_WINDOW = NOT_RUN (actual window geometry not controlled to requested DIP target)
DISCOVERY_REGRESSION = NOT_RUN (QA-6B six-refresh evidence remains; no QA-7 refresh)
LANGUAGE_SWITCH = PASS (preference selection and RU restart verified; prior QA-6B records persistence)
DEV_TOOLS = NOT_RUN on final build
GUI_TESTS = PASS (819)
VISUAL_DEFECTS = 4 corrected; residual count NOT_ESTABLISHED due incomplete matrix
FINAL_HEAD = pending QA-7 commit
WORKTREE = pending QA-7 commit
READY_FOR_MERGE = NO
```
