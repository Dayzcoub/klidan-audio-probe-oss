# L10N-2 Visual QA Report

Date: 2026-11-04  
Branch: `feat/l10n-release-hardening`  
Commit tested: `e28e640c893b015439f6cd002e1eebeee8d55b63`

## Scope and safety

The Release WPF GUI was launched locally with synthetic UI data only. No active
audio test, device change, driver change, or system audio-setting change was
performed. Raw screenshots are retained outside Git and are not included in
this report.

## Automated and interactive results

| Check | Result | Evidence |
|---|---|---|
| Native CTest | PASS | 119/119 passed |
| GUI Core/WPF test project | PASS | `dotnet test`, exit 0 |
| Worker IPC/process test project | PASS | `dotnet test`, exit 0 |
| GUI Release build | PASS | 0 warnings, 0 errors |
| GUI automation | PASS | Real WPF window inspected through Windows UI Automation; language selector, restart, and Developer Tools navigation exercised |
| Language persistence | PASS | English selection persisted after GUI restart |
| Developer Tools hidden without flag | PASS | No Developer Tools section or navigation was exposed in the normal launch |
| Developer Tools with `--developer-tools` | PASS | Developer Tools section and synthetic controls were exposed |
| Russian visual pass at current desktop size | PASS* | Window inspected at 1440x900; no obvious clipping or empty controls observed |
| English visual pass | FAIL | After restart, substantial Russian user-facing text remained in the English UI |
| DPI 100 exact verification | NOT_RUN | Current Windows scale was not changed or independently established |
| DPI 125 exact verification | NOT_RUN | System DPI was not changed |
| DPI 150 exact verification | NOT_RUN | System DPI was not changed |
| Installer English/Russian | PASS* | Existing e28 installer validation remains applicable; no installer source changed in this pass |
| JSON/IPC compatibility | PASS* | Existing e28 regression result; no JSON/IPC source changed in this pass |
| Production isolation | PASS* | Existing e28 regression result; synthetic controls remained Developer Tools-only |
| Privacy / secret scan | PASS* | Existing e28 validation; this report contains no local paths or device identifiers |
| `git diff --check` | PASS | Clean before this report-only change |

`*` indicates a previously executed validation on the same tested commit and
unchanged component, not a newly repeated test in this visual-QA session.

## Localization coverage finding

The current resource-backed localization is incomplete. A static scan found
334 user-facing XAML attributes (`Text`, `Content`, `ToolTip`, or accessibility
name) and 799 Cyrillic string occurrences across the GUI source. These counts
include repeated bindings and diagnostic text, so they are an inventory signal,
not a claim that every occurrence is independently user-visible. The runtime
inspection confirmed the material defect: the English restart still displayed
Russian navigation, instructions, buttons, and diagnostics alongside English
labels.

The existing language selector and persisted preference work correctly, but
they do not satisfy full `en-US` coverage. No JSON field, IPC command,
serialized enum, endpoint identifier, or measurement value was changed.

## Acceptance

```text
L10N_RU = FAIL
L10N_EN = FAIL
HARDCODED_UI_STRINGS = 0      # FAIL: 334 XAML UI attributes remain literal
GUI_AUTOMATION = PASS
VISUAL_QA_RU = PASS           # current desktop size only; full coverage remains incomplete
VISUAL_QA_EN = FAIL
DPI_100 = NOT_RUN
DPI_125 = NOT_RUN
DPI_150 = NOT_RUN
DEV_TOOLS_VISIBILITY = PASS
INSTALLER_L10N = PASS         # prior validation on unchanged installer source
REGRESSION = PASS             # native, GUI Core, Worker, and GUI Release executed here
READY_FOR_MERGE = NO
```

## Minimal follow-up

Move every user-facing XAML/ViewModel/converter string to the existing
resource-backed localization path, add a coverage test that rejects remaining
literal UI attributes, then repeat the English/Russian restart and DPI checks.
The current result is intentionally fail-closed: language selection and
Developer Tools isolation are usable, but the English UI is not fully localized
and exact multi-DPI visual evidence is absent.

## L10N-3 follow-up audit

The Release GUI was launched again from the current branch. Screenshot review
at the available desktop size reproduced the mixed-language defect and also
showed a visual defect in the language selector's displayed value (the compact
template clipped/truncated the selection text). The accessibility tree
confirmed the same Russian messages in the real Windows diagnostics screen.

Concrete blockers are concentrated in:

- `src/gui/KlidanAudioProbe.Gui.App/MainWindow.xaml`: hundreds of literal
  `Text`, `Content`, `ToolTip`, header, and accessibility strings;
- `src/gui/KlidanAudioProbe.Gui.Core/ViewModels.cs`: dynamic status, consent,
  error, scan, profile, evidence, Worker Fake, and inspector messages;
- `src/gui/KlidanAudioProbe.Gui.App/MainWindow.xaml.cs`: dialog titles,
  event/status labels, and converter output strings;
- `src/gui/KlidanAudioProbe.Gui.Core/UiResources.resx` and
  `UiResources.ru-RU.resx`: only the initial shell keys exist, so there is no
  complete key set for the surfaces above.

Because these are production UI paths rather than test-only strings, this
follow-up does not claim completion by applying a blanket runtime translation
or by treating Russian text as an English fallback. No production code was
changed in this follow-up; the report records the reproducible blocker for the
next implementation slice.
