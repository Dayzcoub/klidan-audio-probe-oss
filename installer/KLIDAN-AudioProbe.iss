#define SourceDir GetEnv("KLIDAN_INSTALLER_SOURCE")
#define OutputDir GetEnv("KLIDAN_INSTALLER_OUTPUT")
#define AppVersion GetEnv("KLIDAN_APP_VERSION")

[Setup]
AppId={{A4B8B875-0D1D-4E28-9A41-5C7C5A2E4C01}
AppName=KLIDAN Audio Probe
AppVersion={#AppVersion}
AppVerName=KLIDAN Audio Probe {#AppVersion}
AppPublisher=KLIDAN Audio Probe
AppPublisherURL=https://github.com/Dayzcoub/klidan-audio-probe
DefaultDirName={localappdata}\Programs\KLIDAN Audio Probe
DefaultGroupName=KLIDAN Audio Probe
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=KLIDAN-AudioProbe-Setup
SetupIconFile={#SourceDir}\application-icon.ico
UninstallDisplayIcon={app}\KlidanAudioProbe.Gui.App.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
Uninstallable=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\KLIDAN Audio Probe"; Filename: "{app}\KlidanAudioProbe.Gui.App.exe"; WorkingDir: "{app}"; IconFilename: "{app}\application-icon.ico"
Name: "{autodesktop}\KLIDAN Audio Probe"; Filename: "{app}\KlidanAudioProbe.Gui.App.exe"; WorkingDir: "{app}"; IconFilename: "{app}\application-icon.ico"; Tasks: desktopicon
