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
