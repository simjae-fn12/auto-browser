; Oinzi installer (Inno Setup 6). Build with installer\build.ps1, which publishes to publish-installer first.
; Installs per user (no admin rights), like the browser's own data in %APPDATA%\Oinzi.

#define AppExe "oinzi.exe"
#define Source AddBackslash(SourcePath) + "..\publish-installer"
#define FullVersion GetVersionNumbersString(Source + "\" + AppExe)
; 0.9.1.0 -> 0.9.1
#define AppVersion Copy(FullVersion, 1, RPos(".", FullVersion) - 1)

[Setup]
AppId={{0A3A9DE7-B2ED-4FC2-A4B7-63B8A6E9E5AB}
AppName=Oinzi
AppVersion={#AppVersion}
AppVerName=Oinzi {#AppVersion}
AppPublisher=Oinzi
DefaultDirName={localappdata}\Programs\Oinzi
DefaultGroupName=Oinzi
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=oinzi-setup-{#AppVersion}
SetupIconFile=..\oinzi.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=Oinzi
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; An update closes the running Oinzi first; its tabs come back when it starts again.
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
Name: "{autoprograms}\Oinzi"; Filename: "{app}\{#AppExe}"; AppUserModelID: "oinzi.browser"
Name: "{autodesktop}\Oinzi"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon; AppUserModelID: "oinzi.browser"

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,Oinzi}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Removes the default-browser registration and the start-with-Windows entry. Browsing data in %APPDATA% stays.
Filename: "{app}\{#AppExe}"; Parameters: "--unregister"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterBrowser"

[Code]
// Oinzi draws every page with the WebView2 Runtime. Windows 11 and updated Windows 10 have it; older systems need it once.
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
    if MsgBox('Oinzi에는 Microsoft WebView2 Runtime이 필요합니다. 지금 다운로드 페이지를 열까요?', mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://developer.microsoft.com/microsoft-edge/webview2/#download', '', '', SW_SHOWNORMAL, ewNoWait, Code);
end;

// Uninstalling keeps Oinzi's data (logins, profiles, settings) unless the person asks to remove it, so a reinstall can
// carry on. Claude Code and Codex logins live in their own folders (~\.claude, ~\.codex) and are never touched here.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep <> usPostUninstall then exit;
  Data := ExpandConstant('{userappdata}\Oinzi');
  if not DirExists(Data) then exit;
  if UninstallSilent then exit;
  if MsgBox('Oinzi의 설정·브라우저 로그인·프로필·대화 기록까지 지울까요?' + #13#10 + #13#10 +
            '아니요를 누르면 다시 설치했을 때 그대로 이어서 쓸 수 있어요.' + #13#10 +
            '(Claude Code·Codex 로그인은 Oinzi 것이 아니라서 지우지 않아요.)', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
  begin
    DelTree(Data, True, True, True);
    DelTree(ExpandConstant('{userappdata}\pluck-windows-native'), True, True, True);
  end;
end;
