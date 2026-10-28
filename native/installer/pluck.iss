; pluck installer (Inno Setup 6). Build with installer\build.ps1, which publishes to publish-installer first.
; Installs per user (no admin rights), like the browser's own data in %APPDATA%\pluck-windows-native.

#define AppExe "pluck.exe"
#define Source AddBackslash(SourcePath) + "..\publish-installer"
#define AppVersion GetVersionNumbersString(Source + "\" + AppExe)

[Setup]
AppId={{0A3A9DE7-B2ED-4FC2-A4B7-63B8A6E9E5AB}
AppName=pluck
AppVersion={#AppVersion}
AppVerName=pluck {#AppVersion}
AppPublisher=pluck
DefaultDirName={localappdata}\Programs\pluck
DefaultGroupName=pluck
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=pluck-setup-{#AppVersion}
SetupIconFile=..\pluck.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=pluck
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; An update closes the running pluck first; its tabs come back when it starts again.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.xml"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\pluck"; Filename: "{app}\{#AppExe}"; AppUserModelID: "pluck.browser"
Name: "{autodesktop}\pluck"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon; AppUserModelID: "pluck.browser"

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,pluck}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Removes the default-browser registration and the start-with-Windows entry. Browsing data in %APPDATA% stays.
Filename: "{app}\{#AppExe}"; Parameters: "--unregister"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterBrowser"

[Code]
// pluck draws every page with the WebView2 Runtime. Windows 11 and updated Windows 10 have it; older systems need it once.
function WebView2Installed(): Boolean;
var
  Version: String;
begin
  Result :=
    RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')
    or RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if (CurStep = ssPostInstall) and not WebView2Installed() then
    if MsgBox('pluck에는 Microsoft WebView2 Runtime이 필요합니다. 지금 다운로드 페이지를 열까요?', mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://developer.microsoft.com/microsoft-edge/webview2/#download', '', '', SW_SHOWNORMAL, ewNoWait, Code);
end;
