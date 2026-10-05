; Inno Setup script for Empyrion Server Manager.
; Built by release.ps1:  ISCC /DAppVersion=x.y.z installer\EmpyrionServerManager.iss
; Expects the published app in ..\dist\stage (build.ps1 creates it).
;
; Installs per user (no admin needed) into %LOCALAPPDATA%\Programs\Empyrion Server Manager,
; so the built-in updater can replace it silently. manager-settings.json is never installed or removed,
; so updates keep your configuration.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppName "Empyrion Server Manager"
#define AppExe "EmpyrionManager.exe"
#define Repo "https://github.com/AndrewBrooks1988/empyrion-server-manager"

[Setup]
AppId={{8C3B1C62-6E8B-4C9A-9C4E-3E6A9F1D2B47}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=AndrewBrooks1988
AppPublisherURL={#Repo}
AppSupportURL={#Repo}/issues
AppUpdatesURL={#Repo}/releases
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=EmpyrionServerManager-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
CloseApplications=force
RestartApplications=no
VersionInfoVersion={#AppVersion}

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked
Name: "autostart"; Description: "Start the manager when I sign in to Windows (recommended for a server PC)"; Flags: unchecked

[Files]
Source: "..\dist\stage\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "manager-settings.json,*.bak"
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Flags: runminimized
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon; Flags: runminimized
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; Parameters: "--no-browser"; WorkingDir: "{app}"; Tasks: autostart; Flags: runminimized

[Run]
; normal install: offer to start it (it opens the dashboard in the browser)
Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent runminimized
; silent update from the dashboard (/RELAUNCH=1): start it again in the background
Filename: "{app}\{#AppExe}"; Parameters: "--no-browser"; WorkingDir: "{app}"; Flags: nowait runminimized; Check: IsRelaunch

[UninstallRun]
; stop only the copy installed here (never a portable copy or another install)
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -Command ""Get-Process EmpyrionManager -ErrorAction SilentlyContinue | Where-Object {{ $_.Path -like '{app}\*' }} | Stop-Process -Force"""; Flags: runhidden; RunOnceId: "StopManager"

[UninstallDelete]
; precompressed web assets from older builds
Type: files; Name: "{app}\wwwroot\*.br"
Type: files; Name: "{app}\wwwroot\*.gz"

[Messages]
FinishedLabel=Setup has finished installing [name].%n%nThe dashboard runs at http://127.0.0.1:8090. Open Settings -> Setup to point it at your dedicated server.%n%nYour settings (manager-settings.json) are kept when you update or uninstall.

[Code]
function IsRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;
