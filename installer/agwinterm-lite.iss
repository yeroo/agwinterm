; agwinterm lite installer (Inno Setup 6) — per-user, no admin. Lite ships standalone here:
; its own copy of the shared Rust pty-host + core dll, plus its bundled fonts + toolbar icons.
; Built via installer\build.ps1 (stages to stage-lite\ then runs ISCC on this file).

#define AppName    "agwinterm lite"
#define AppVersion "0.16.6"
#define AppExe     "agwinterm-lite.exe"
#define AppPublisher "Boris Kudriashov"

[Setup]
AppId={{B8F4A2D3-6C1E-4F7B-9D32-4A1E8B5C9F26}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\agwinterm-lite
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\lite\assets\agwinterm-lite.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
OutputDir=Output
OutputBaseFilename=agwinterm-lite-setup-{#AppVersion}

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "stage-lite\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[InstallDelete]
; ≤0.16.2 shipped the main app's icon file for the shortcuts; the icon is embedded in the exe now.
Type: files; Name: "{app}\agwinterm.ico"

[Icons]
; No IconFilename: the exe embeds the lite icon (VGA black + cyan), shortcuts pick it up.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}";  Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
