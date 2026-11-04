# Windows installer

KLIDAN Audio Probe is distributed as one per-user, unsigned Inno Setup installer for Windows 10/11 x64. The installer contains the self-contained WPF application, the separate `KlidanProbeWorker.exe`, the native `KlidanAudioProbe.exe`, the .NET runtime files, and the application icon.

Build from a clean, exact branch and commit with:

```powershell
.\scripts\Build-LabInstaller.ps1 -ExpectedHead <full-commit-sha>
```

The script requires CMake, the .NET SDK, `dumpbin.exe`, and Inno Setup 6 (`ISCC.exe`) to already be installed. It never downloads or installs dependencies. It configures CMake with `--fresh`, builds both native Release targets, rejects external MSVC CRT imports, publishes self-contained `win-x64` GUI output, and refuses to overwrite an existing Setup.exe.

The default installation is per-user under `%LOCALAPPDATA%\Programs\KLIDAN Audio Probe`. It creates a Start Menu shortcut and offers an unchecked desktop shortcut. Installation does not start the Worker, enumerate devices, or run audio operations. Uninstall removes only installed files and shortcuts; it does not remove `%LOCALAPPDATA%\KlidanAudioProbe\Profiles`, `Scans`, `Evidence`, `SustainedDuplex`, or other operator data.

The Worker remains a separate process. The GUI expects `KlidanProbeWorker.exe` beside the GUI executable, and the Worker expects `KlidanAudioProbe.exe` beside itself. The installer preserves both paths.

The installer is unsigned. Windows SmartScreen or Defender may display an unsigned-publisher warning. No Authenticode or release-certification claim is made. Verify the external `.manifest.json` SHA-256 before transfer.

The supplied operator artwork is preserved as `assets/application-icon.png`; `assets/application-icon.ico` contains PNG-backed 16, 24, 32, 48, 64, 128 and 256 pixel frames. Installer compilation and install/uninstall QA require a Windows machine with Inno Setup; if unavailable, report them as NOT_RUN rather than inferring success from a build or package manifest.
