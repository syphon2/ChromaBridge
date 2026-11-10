; Build: dotnet publish GameLightSync.csproj -c Release -o publish
;        ISCC.exe installer\GameLightSync.iss   ->  installer\Output\GameLightSync-Setup-<version>.exe
#define AppName "GameLightSync"
#define AppVersion "0.1.0"
#define AppExe "GameLightSync.exe"

[Setup]
AppId={{6F1C8E3A-5B0D-4E52-9C77-2D4A9B1E7F30}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=GameLightSync contributors
AppPublisherURL=https://github.com/syphon2/GameLightSync
; per-user install: the app writes bridge.json next to the exe, so it must not live in Program Files
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
LicenseFile=..\LICENSE
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#AppExe}
AppMutex=Local\GameLightSync.SingleInstance
CloseApplications=yes
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked
Name: "runatlogon"; Description: "Start {#AppName} when I sign in"; Flags: unchecked

[Files]
; settings (bridge.json) and logs are created at runtime and are never overwritten or removed
Source: "..\publish\*"; DestDir: "{app}"; Excludes: "bridge.json,log.txt,keyboard-state.txt,*.pdb,Logitech*.dll"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"""; Tasks: runatlogon; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
function DesktopRuntime8Installed: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Copy(Names[I], 1, 2) = '8.' then Result := True;
end;

function InitializeSetup: Boolean;
var
  Err: Integer;
begin
  Result := True;
  if not DesktopRuntime8Installed then
    if MsgBox('GameLightSync needs the .NET 8 Desktop Runtime (x64), which was not found.' + #13#10 +
              'Open the download page now? Setup will continue afterwards.', mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0', '', '', SW_SHOWNORMAL, ewNoWait, Err);
end;
