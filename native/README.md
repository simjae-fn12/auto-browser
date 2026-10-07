# Ego Windows Native

Windows Forms와 Microsoft Edge WebView2로 만든 Windows 브라우저입니다. Electron은 사용하지 않습니다. 원본 `ego-lite`의 공식 포트가 아닌 독립 구현입니다.

## 빌드와 실행

```powershell
dotnet run --project .\native.csproj
dotnet publish .\native.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish
```

빌드에는 .NET 10 SDK가 필요합니다. 실행 파일은 `publish\EgoWindowsNative.exe`입니다. Windows의 WebView2 런타임이 필요합니다. 모든 탭은 `%APPDATA%\ego-windows-native\profile`을 공유하므로 앱 안에서 로그인한 사이트를 에이전트 탭도 사용할 수 있습니다. 사람 탭과 에이전트 작업 공간의 탭 URL은 다시 시작할 때 복원됩니다.

## 에이전트 CLI

별도 PowerShell에서 다음을 실행합니다. 새 에이전트 탭은 현재 선택된 탭을 바꾸지 않습니다.

```powershell
.\cli.ps1 list
.\cli.ps1 new https://example.com
.\cli.ps1 snapshot 2
.\cli.ps1 click 2 '@f0:1'
.\cli.ps1 fill 2 '@f0:2' 'hello'
.\cli.ps1 press 2 Enter
.\cli.ps1 scroll 2 down 600
.\cli.ps1 show 2
.\cli.ps1 screenshot 2 .\tab.png
```

`snapshot`의 참조 번호는 페이지나 프레임이 바뀌면 새로 받아야 합니다. CSS 선택자도 지원합니다. 제어 API는 `127.0.0.1`에만 열리고 연결 토큰은 사용자 프로필에 저장됩니다.
기본 스냅샷은 본문 4,000자와 조작 요소 80개로 제한합니다. 전체 내용이 필요하면 `browser.snapshot(id, { full: true })` 또는 `.\cli.ps1 snapshot 2 full`을 사용하세요.

여러 단계의 자동화는 `agent.mjs`에 JavaScript를 한 번 전달해 실행합니다. 각 단계는 로컬 브라우저와 통신하고, 중간 판단은 스크립트 안에서 처리합니다.

```powershell
@'
const task = await browser.useOrCreateSpace('example research');
const { id } = await task.open('https://example.com');
try {
  await task.wait(id, 'a');
  const page = await task.snapshot(id);
  console.log(page.title, page.elements.map(element => element.text));
} finally {
  await task.close(id);
}
'@ | node .\agent.mjs
```

`node .\agent.mjs .\my-task.mjs`처럼 파일로 전달할 수도 있습니다. 먼저 `browser.useOrCreateSpace('작업 이름')`으로 작업 공간을 가져옵니다. 반환된 `task`에는 `list`, `open`, `show`, `goto`, `snapshot`, `click`, `fill`, `press`, `scroll`, `wait`, `js`, `screenshot`, `close`가 있습니다. 같은 이름을 다시 사용하면 기존 탭을 이어서 사용할 수 있습니다. 다른 작업 공간의 탭 ID는 조작할 수 없습니다. Node.js는 이 자동화 스크립트 실행에만 사용하며 브라우저 앱 자체에는 필요하지 않습니다.

작업 공간마다 탭과 페이지 입력 포커스·스크롤 상태가 따로 유지됩니다. 에이전트가 새 탭을 열어도 사용자가 보고 있는 탭은 바뀌지 않습니다. 모든 작업 공간은 현재 같은 WebView2 프로필을 사용하므로 로그인 쿠키와 사이트 저장소는 공유됩니다. 사이트 계정 자체까지 작업별로 분리하는 기능은 아직 없습니다.
앱을 닫으면 사용자 탭과 작업 공간 이름·탭 URL을 저장하고 다음 실행 때 복원합니다. 페이지 안의 입력 중인 내용과 스크롤 위치는 재시작 후 복원되지 않습니다.
사이트가 `window.open()`으로 여는 팝업은 같은 작업 공간의 새 탭에 연결됩니다. 팝업에서 `window.opener` 통신과 `window.close()`가 동작합니다.

## 현재 범위

탭/주소창/검색/뒤로/앞으로, 프로필 공유, 에이전트 탭, 페이지 스냅샷과 iframe, 클릭/입력/대기/스크린샷/JavaScript 실행을 구현했습니다. Chrome 프로필 이전, 확장 프로그램 관리, 공식 `ego-browser` 스킬과의 호환은 아직 구현되지 않았습니다. WebView2는 Edge 렌더링 엔진을 사용합니다.
