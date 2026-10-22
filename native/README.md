# pluck Windows Native

Windows Forms와 Microsoft Edge WebView2로 만든 Windows 브라우저입니다. Electron은 사용하지 않습니다.

## 빌드와 실행

```powershell
dotnet run --project .\native.csproj
dotnet publish .\native.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish
```

빌드에는 .NET 10 SDK가 필요합니다. 실행 파일은 `publish\pluck.exe`입니다. Windows의 WebView2 런타임이 필요합니다. 모든 탭은 `%APPDATA%\pluck-windows-native\profile`을 공유하므로 앱 안에서 로그인한 사이트를 에이전트 탭도 사용할 수 있습니다. 다시 시작할 때는 사람 탭의 URL만 복원됩니다.

## 화면과 단축키

사이드바·주소 표시·커맨드 팔레트는 `chrome/chrome.html`을 별도 WebView2로 그린 UI입니다. 실행 파일에 리소스로 포함되며, 페이지 WebView는 이 UI가 알려 주는 영역 위에 배치됩니다. 시스템 라이트/다크 테마를 따르고 창 제목 표시줄 색도 맞춥니다.

| 단축키 | 동작 |
| --- | --- |
| `Ctrl+T` | 커맨드 팔레트로 새 탭 (검색·주소·열린 탭 전환) |
| `Ctrl+L` | 현재 탭 주소 편집 |
| `Ctrl+K` | 제목·주소로 열린 탭 검색 |
| `Ctrl+W` / `Ctrl+Shift+T` | 탭 닫기 / 닫은 탭 다시 열기 |
| `Ctrl+Tab`, `Ctrl+1`~`9` | 탭 전환 |
| `Ctrl+Shift+S` | 사이드바 접기·펼치기 |
| `Ctrl+R`, `F5`, `Alt+←/→` | 새로고침, 뒤로/앞으로 |
| `Ctrl+J` | AI 패널 열기·닫기 |

### AI 패널

오른쪽 패널에서 Claude와 Codex에게 바로 작업을 맡길 수 있습니다. 앱이 이미 로그인된 로컬 CLI(`claude`, `codex`)를 헤드리스로 실행하므로 구독 중인 계정을 그대로 씁니다. 별도 API 키는 필요 없습니다. 두 CLI와 Node.js가 설치돼 있어야 합니다.

- AI는 `%APPDATA%\pluck-windows-native\assistant\claude` 또는 `assistant\codex` 작업 폴더에서 실행되고, 여기에 놓인 `agent.mjs`로 브라우저를 조작합니다. 실행할 때 지침 파일을 갱신하고 부모 터미널·에이전트의 세션 환경 변수를 제거합니다. 기존 CLI 로그인 위치는 유지합니다.
- "현재 탭" 칩이 켜져 있으면 보고 있는 탭 정보가 함께 전달되어 "이 페이지 요약해줘" 같은 요청이 가능합니다. 이를 위해 `browser.activeTab()` API를 추가했습니다.
- AI가 직접 연 탭은 사이드바의 `Claude`/`Codex` 그룹에 나타납니다. 새 대화 버튼은 이전 방을 보존합니다. 상단 대화 목록에서 이전 방을 고르면 내용과 CLI 세션이 복원되어 이어서 대화할 수 있습니다. 대화는 `assistant-history.json`에 저장하며 앱을 다시 열어도 남습니다. 실행 중 새 대화를 열면 기존 작업을 중지합니다.
- 조작 중 페이지 테두리·상태 배지, 대상 요소 강조, 가상 커서와 클릭 파동이 표시됩니다. 실제 시스템 마우스를 이동하지 않습니다. `AI가 조작하는 탭 따라보기`를 끄면 현재 보고 있는 탭을 유지합니다. 임의 `js` 명령은 작업 상태만 표시하며 구체적인 클릭 위치를 추적하지 않습니다.
- 대화 저장은 600ms 단위로 묶어 백그라운드에서 처리하고 정상 종료 시 남은 내용을 저장합니다. 스트리밍 Markdown은 최대 초당 20회 갱신하며, 입력할 때 변경되지 않은 대화 목록과 아이콘을 다시 만들지 않습니다.
- 권한: Claude는 작업 폴더 안 파일 편집과 `node` 명령, 웹 검색만 자동 허용됩니다. Codex는 `workspace-write` 샌드박스에 로컬 네트워크를 허용해 실행합니다.

탭은 드래그로 순서를 바꿀 수 있고(재시작 후에도 유지), 가운데 클릭으로 닫을 수 있습니다. AI 작업 공간은 접을 수 있으며 에이전트가 명령을 실행 중이면 보라색 점이 표시됩니다.

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

### 자동 탭 정리

- 기본적으로 스크립트가 성공하거나 오류로 끝나면 **해당 실행에서 새로 연 탭과 팝업**을 닫고 WebView2를 해제합니다. 같은 Space의 다른 실행이 연 탭과 사용자 탭은 건드리지 않습니다.
- 여러 번의 CLI 실행에 걸쳐 이어서 작업해야 할 때만 `node .\agent.mjs --keep-tabs .\my-task.mjs`를 사용합니다. 표준 입력에도 `--keep-tabs`를 쓸 수 있습니다. 이 옵션도 오류 종료 시에는 새로 연 탭을 정리합니다. 마지막 단계에서는 남겨 둔 탭에 `task.close(id)`를 호출하세요.
- 실행 중에는 30초마다 생존 신호를 보냅니다. 강제 종료나 기존 CLI 사용으로 남은 AI 탭은 **작업 공간 전체가 15분 동안 사용되지 않으면** 최대 1분 이내에 정리합니다. 명령 실행 중이거나 사용자가 현재 보고 있는 탭이 있는 공간은 제외합니다.
- AI 탭은 Space당 20개, 전체 60개까지만 열 수 있습니다. 한도에 도달하면 기존 탭을 닫으라는 오류를 반환합니다. 팝업도 같은 한도를 적용합니다.
- 앱 재시작 시 AI 탭은 복원하지 않습니다. 사용자 탭과 로그인 프로필은 유지됩니다.
- 초기화 실패 상태의 탭도 `close`로 정리할 수 있습니다.

작업 공간마다 탭과 페이지 입력 포커스·스크롤 상태가 따로 유지됩니다. 에이전트가 새 탭을 열어도 사용자가 보고 있는 탭은 바뀌지 않습니다. 모든 작업 공간은 현재 같은 WebView2 프로필을 사용하므로 로그인 쿠키와 사이트 저장소는 공유됩니다. 사이트 계정 자체까지 작업별로 분리하는 기능은 아직 없습니다.
앱을 닫으면 사용자 탭 URL만 저장하고 다음 실행 때 복원합니다. 페이지 안의 입력 중인 내용과 스크롤 위치는 재시작 후 복원되지 않습니다.
사이트가 `window.open()`으로 여는 팝업은 같은 작업 공간의 새 탭에 연결됩니다. 팝업에서 `window.opener` 통신과 `window.close()`가 동작합니다.

## 현재 범위

탭/주소창/검색/뒤로/앞으로, 프로필 공유, 에이전트 탭, 페이지 스냅샷과 iframe, 클릭/입력/대기/스크린샷/JavaScript 실행을 구현했습니다. Chrome 프로필 이전, 확장 프로그램 관리, 공식 `pluck-browser` 스킬과의 호환은 아직 구현되지 않았습니다. WebView2는 Edge 렌더링 엔진을 사용합니다.
