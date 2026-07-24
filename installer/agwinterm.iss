; agwinterm installer (Inno Setup 6) — per-user, no admin.
; Build via installer\build.ps1 (publishes to stage\ then runs ISCC on this file).

#define AppName    "agwinterm"
#define AppVersion "0.14.7"
#define AppExe     "Agwinterm.Win32.exe"
#define LiteExe    "agwinterm-lite.exe"
#define AppPublisher "Boris Kudriashov"
; Both terminals default new sessions to the Rust pty-host. The main app reads this as a
; first-run seed only (it never overrides an existing config); lite always uses the Rust host.
#define RustHostArg "--default-session-host server-rust"

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
Name: "desktopicon"; Description: "Create &desktop shortcuts (both terminals)"; GroupDescription: "Shortcuts:"

[Files]
Source: "stage\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
; Main terminal — its shortcut seeds server-rust on a first run (harmless once configured).
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Parameters: "{#RustHostArg}"; IconFilename: "{app}\assets\agwinterm.ico"
Name: "{autodesktop}\{#AppName}";  Filename: "{app}\{#AppExe}"; Parameters: "{#RustHostArg}"; IconFilename: "{app}\assets\agwinterm.ico"; Tasks: desktopicon
; Lite terminal — always rides the Rust pty-host; uses the main app's icon.
Name: "{autoprograms}\{#AppName} lite"; Filename: "{app}\{#LiteExe}"; IconFilename: "{app}\assets\agwinterm.ico"
Name: "{autodesktop}\{#AppName} lite";  Filename: "{app}\{#LiteExe}"; IconFilename: "{app}\assets\agwinterm.ico"; Tasks: desktopicon

[Run]
; Launch-on-finish checkbox (interactive installs only) — passes the seed so the very first run
; (typically this launch) defaults the main app to the Rust pty-host.
Filename: "{app}\{#AppExe}"; Parameters: "{#RustHostArg}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
