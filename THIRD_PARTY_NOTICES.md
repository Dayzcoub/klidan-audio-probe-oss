# Third-Party Notices

This file covers third-party components included in or required to build or
distribute KLIDAN Audio Probe. `LICENSE` applies only to KLIDAN-owned
components and does not relicense the components below.

## RapidJSON 1.1.0

Source: `src/third_party/rapidjson`  
Copyright: THL A29 Limited (a Tencent company) and Milo Yip  
License: MIT; vendored `msinttypes` component: BSD.

| Version | Full source notice | SHA-256 | Source snapshot | Installer |
|---|---|---|---|---|
| 1.1.0 | `src/third_party/rapidjson/license.txt` | `55972017b8f8bffee8922b5be4ba582ac401a3b70b5e62a4b8fbea56342b0500` | present unchanged | compiled into native binaries; attribution present in installer `THIRD_PARTY_NOTICES.md` |

The complete original notice and license text is preserved in
[`src/third_party/rapidjson/license.txt`](src/third_party/rapidjson/license.txt).
The vendored tree does not include `bin/jsonchecker`; its JSON-license text
remains in the preserved upstream notice for provenance only.

## Microsoft .NET runtime

The GUI targets `net10.0-windows` and the verified self-contained publish uses:

| Component | Exact version | Source package | License/notice file | SHA-256 | Package presence |
|---|---|---|---|---|---|
| Microsoft.NETCore.App | 10.0.10 | `microsoft.netcore.app.runtime.win-x64` | `LICENSE.TXT` | `d7a68596ab69b06f51ca278a6545148e4269a9381c26d597c13df5d88e08cf5b` | copied by installer script |
| Microsoft.NETCore.App | 10.0.10 | `microsoft.netcore.app.runtime.win-x64` | `THIRD-PARTY-NOTICES.TXT` | `6d15e10a101c6bfff2ab4429ed061bf76c456fc4b23ad6b03e0d0f8377148a21` | copied by installer script |
| Microsoft.WindowsDesktop.App / WPF | 10.0.10 | `microsoft.windowsdesktop.app.runtime.win-x64` | `LICENSE` | `a89886665765362eb77e0f8e26602c924520041d1711b2eedc136434fe4d01ab` | copied by installer script |

The original files are preserved unchanged in the local verification artifact
`out/os-1-4-runtime-notices-20261104/`. The publish identity is recorded in
`KlidanAudioProbe.Gui.App.runtimeconfig.json` as `Microsoft.NETCore.App`
10.0.10 and `Microsoft.WindowsDesktop.App` 10.0.10. The packaging script
copies the project `LICENSE` and this notice file plus the exact runtime-pack
license/notice files into the installer source directory and records their
hashes in the package manifest.

The runtime pack license text is the .NET Foundation MIT license. The
`THIRD-PARTY-NOTICES.TXT` file retains the original Microsoft runtime
component notices and must not be edited or replaced by the Apache project
license.

## Windows SDK / WASAPI / COM system APIs

Native targets link Windows system libraries (`ole32`, `propsys`, `setupapi`,
`uuid`, `avrt`, and `bcrypt`). They are platform APIs, not vendored source or
bundled third-party binaries. Do not imply Microsoft endorsement.

## Inno Setup

Inno Setup 6 (`ISCC.exe`) is a build-time prerequisite only. It is not copied
into the application installer or runtime package; its own license applies to
the operator's build-tool installation.

## NuGet and package managers

No `PackageReference`, `packages.config`, NuGet theme, icon package, font
package or other package-manager dependency was found in the tracked projects.
