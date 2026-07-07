; agwinterm installer (Inno Setup 6) — per-user, no admin.
; Build via installer\build.ps1 (publishes to stage\ then runs ISCC on this file).

#define AppName    "agwinterm"
#define AppVersion "0.4.0"
#define AppExe     "Agwinterm.Win32.exe"
#define AppPublisher "Boris Kudriashov"

[Setup]
AppId={{A7E3F1C2-5B9D-4E6A-8C21-3F0D9B4A7E15}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\agwinterm
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\src\Agwinterm.Win32\assets\agwinterm.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
OutputDir=Output
OutputBaseFilename=agwinterm-setup-{#AppVersion}

; Minimal, non-invasive setup (agterm-style): only copy files + create shortcuts. Integrations
; (agwintermctl on PATH, agent hooks, agent skill, shell integration) are OPT-IN from inside the
; app — action palette (Ctrl+Shift+P) -> the "Install ..." entries — so setup never edits PATH or
; writes to the user's profile/config behind their back.

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "stage\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\assets\agwinterm.ico"
Name: "{autodesktop}\{#AppName}";  Filename: "{app}\{#AppExe}"; IconFilename: "{app}\assets\agwinterm.ico"; Tasks: desktopicon

[Run]
; Launch-on-finish checkbox (interactive installs only).
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
