; Per-user installer: no admin rights, and no UAC prompt under winget.
; Build dist first (see README), then run ISCC on this file.

#define AppVersion "1.0.0"

[Setup]
AppId={{7C3E9A52-4F1D-4B86-9E27-1D5A8C60F3B4}
AppName=LockOsd
AppVersion={#AppVersion}
AppPublisher=edglz
DefaultDirName={localappdata}\Programs\LockOsd
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22621
OutputDir=..\artifacts
OutputBaseFilename=LockOsd-Setup-{#AppVersion}-x64
Compression=lzma2/max
SolidCompression=yes
UninstallDisplayIcon={app}\LockOsd.exe
CloseApplications=no

[Files]
Source: "..\dist\*"; Excludes: "*.pdb"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "LockOsd"; ValueData: """{app}\LockOsd.exe"""; Flags: uninsdeletevalue

[Run]
Filename: "{app}\LockOsd.exe"; Flags: nowait

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM LockOsd.exe /F"; Flags: runhidden; RunOnceId: "StopLockOsd"

[Code]
// The app has no window to close and locks its files, so an upgrade has to stop it first.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM LockOsd.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
