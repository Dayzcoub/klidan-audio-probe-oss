# Building KLIDAN Audio Probe

Current implementation: Windows 10/11 x64, C++20/CMake and .NET `net10.0` /
`net10.0-windows`. Linux can inspect source and documents but cannot validate
the Windows WASAPI build.

## Prerequisites

- Visual Studio 2022 C++ build tools and Windows SDK.
- CMake with the x64 generator.
- .NET 10 SDK.
- PowerShell 5+.
- Inno Setup 6 only for the installer.

The repository does not download or install dependencies. See
`docs/WINDOWS_INSTALLER.md` for the checked installer workflow.

## Native Release build

```powershell
cmake -S . -B build -A x64
cmake --build build --config Release --parallel
ctest --test-dir build -C Release --output-on-failure
```

## GUI tests

```powershell
dotnet run --project tests/gui/KlidanAudioProbe.Gui.Core.Tests/KlidanAudioProbe.Gui.Core.Tests.csproj -c Release
dotnet run --project tests/gui/KlidanAudioProbe.Gui.Worker.Tests/KlidanAudioProbe.Gui.Worker.Tests.csproj -c Release
```

These tests use synthetic fixtures and do not replace physical Windows audio
QA. Follow `docs/TEST_SAFETY.md` before any active operation.

## Installer

Use `scripts/Build-LabInstaller.ps1` with an expected branch and HEAD. It
requires `ISCC.exe`, audits native dependencies, publishes self-contained
`win-x64` GUI output, and refuses to overwrite an existing installer.
