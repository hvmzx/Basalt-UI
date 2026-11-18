; Inno Setup script for Basalt UI.
; Built by build-installer.ps1, which publishes the single-file exe into ..\publish first.
; The version is passed in with /DMyAppVersion=X.Y.Z by the build script or the workflow.

#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

#define MyAppName "Basalt UI"
#define MyAppExeName "BasaltUI.exe"
#define MyAppPublisher "hvmzx"
#define MyAppURL "https://github.com/hvmzx/Basalt-UI"

[Setup]
AppId={{CB5CC6D4-CF0C-4370-ACE2-7E4EE967A241}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
; Per-user install: no UAC prompt, and Windows accepts the tray icon from here.
DefaultDirName={localappdata}\Programs\BasaltUI
DefaultGroupName={#MyAppName}
OutputDir=..\artifacts
OutputBaseFilename=BasaltUI-{#MyAppVersion}-setup
SetupIconFile=..\src\BasaltUI\Assets\basalt.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesInstallIn64BitMode=x64
AllowNoIcons=yes
; A running copy holds the exe open. We stop just our own installed copy below.
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\BasaltUI.exe"; DestDir: "{app}"; DestName: "{#MyAppExeName}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// Stop only the copy installed in {app}: a Basalt UI the user may be running from
// somewhere else is none of this installer's business. A PowerShell helper is
// written to {tmp} first so there is no quoting to get wrong on the command line.
procedure StopRunningCopy;
var
  Helper, Target, Body: String;
  Code: Integer;
begin
  Target := ExpandConstant('{app}');
  Helper := ExpandConstant('{tmp}\stop-running.ps1');
  Body := '$d = ''' + Target + '''' + #13#10 +
          'Get-Process BasaltUI -ErrorAction SilentlyContinue | ' +
          'Where-Object { $_.Path -and $_.Path.StartsWith($d, [StringComparison]::OrdinalIgnoreCase) } | ' +
          'Stop-Process -Force -ErrorAction SilentlyContinue';
  if SaveStringToFile(Helper, Body, False) then
    Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
         '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + Helper + '"',
         '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningCopy;
  Result := '';
end;

function InitializeUninstall: Boolean;
begin
  StopRunningCopy;
  Result := True;
end;

// The in-app updater launches us with /RELAUNCH once it has closed, so the new copy is
// actually running when the update finishes instead of waiting for a Start menu click.
function HasParam(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if (CurStep = ssDone) and HasParam('/RELAUNCH') then
    Exec(ExpandConstant('{app}\{#MyAppExeName}'), '', '', SW_SHOWNORMAL, ewNoWait, Code);
end;
