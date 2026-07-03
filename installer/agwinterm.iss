; agwinterm installer (Inno Setup 6) — per-user, no admin.
; Build via installer\build.ps1 (publishes to stage\ then runs ISCC on this file).

#define AppName    "agwinterm"
#define AppVersion "0.1.0"
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

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "addtopath";  Description: "Add agwintermctl to my &PATH (for shells & AI agents)"; GroupDescription: "Integration:"

[Files]
Source: "stage\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\assets\agwinterm.ico"
Name: "{autodesktop}\{#AppName}";  Filename: "{app}\{#AppExe}"; IconFilename: "{app}\assets\agwinterm.ico"; Tasks: desktopicon

[Registry]
; Append {app} to the per-user PATH (Inno auto-broadcasts WM_SETTINGCHANGE for the Environment key,
; so newly launched shells pick it up). Guarded so it isn't added twice.
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; \
  ValueData: "{olddata};{app}"; Flags: preservestringtype; \
  Tasks: addtopath; Check: NeedsAddPath(ExpandConstant('{app}'))

[Run]
; Auto-install the agent skill (agwintermctl runs install skill locally — no running app needed;
; works in silent installs too). Non-fatal if it fails.
Filename: "{app}\agwintermctl.exe"; Parameters: "install skill"; StatusMsg: "Installing agent skill..."; Flags: runhidden
; Launch-on-finish checkbox (interactive installs only).
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
const EnvKey = 'Environment';

function NeedsAddPath(Param: string): Boolean;
var Orig: string;
begin
  if not RegQueryStringValue(HKCU, EnvKey, 'Path', Orig) then
  begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Uppercase(Param) + ';', ';' + Uppercase(Orig) + ';') = 0;
end;

// Rebuild the user PATH without {app} on uninstall.
procedure EnvRemovePath(PathToRemove: string);
var Paths, Res, Cur: string; i: Integer;
begin
  if not RegQueryStringValue(HKCU, EnvKey, 'Path', Paths) then exit;
  Res := '';
  Paths := Paths + ';';
  while Pos(';', Paths) > 0 do
  begin
    i := Pos(';', Paths);
    Cur := Copy(Paths, 1, i - 1);
    Delete(Paths, 1, i);
    if (Cur <> '') and (Uppercase(Cur) <> Uppercase(PathToRemove)) then
    begin
      if Res <> '' then Res := Res + ';';
      Res := Res + Cur;
    end;
  end;
  RegWriteExpandStringValue(HKCU, EnvKey, 'Path', Res);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    EnvRemovePath(ExpandConstant('{app}'));
end;
