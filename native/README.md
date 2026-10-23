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
| `Ctrl+Shift+O` | 모든 세션 개요 열기·닫기 |

### 세션 개요

사이드바 위쪽의 격자 버튼이나 `Ctrl+Shift+O`로 모든 세션을 카드로 봅니다. 내 탭, AI 패널의 각 대화, 터미널 에이전트가 각각 카드 하나이며, 세션이 마지막으로 다룬 탭의 썸네일과 탭 수를 보여 줍니다. 열려 있는 동안 2초마다 갱신합니다.

- 작업 중인 세션에는 진행 상태와 **넘겨받기**·**중지** 버튼이 표시됩니다. 터미널 세션은 최근 45초 안에 명령이나 생존 신호가 있으면 작업 중으로 봅니다.
- **중지**: AI 패널 대화는 실행 중인 CLI를 종료합니다. 터미널 에이전트는 프로세스를 직접 끌 수 없으므로 그 세션의 이후 명령을 모두 거절해 멈춥니다. 중지된 세션의 탭은 닫지 않고 남겨 둡니다.
- **넘겨받기**: 중지한 뒤 그 세션의 탭으로 이동해 직접 이어서 조작할 수 있게 합니다.
- 카드를 누르거나 방향키로 고른 뒤 Enter를 누르면 그 세션의 탭으로 이동합니다. Esc로 닫습니다.
- 썸네일은 탭이 로드되거나 에이전트가 조작한 직후 미리 찍어 둡니다. 화면에 보이지 않는 탭도 DevTools 캡처로 찍습니다.

### 녹화

AI 패널 위쪽의 녹화 버튼(그 대화의 세션)이나 개요 카드의 **녹화**로 세션이 브라우저에서 하는 일을 녹화합니다. 녹화 중인 세션은 사이드바 그룹 이름에 `녹화 중`이 붙습니다.

- 세션이 작업 중인 탭을 0.7초마다, 그리고 에이전트 명령이 끝날 때마다 DevTools로 캡처합니다. 다른 탭을 보고 있어도 녹화되며, 화면이 바뀌지 않으면 저장하지 않습니다. 에이전트 동작(이동·클릭·입력·스크롤)은 화면에 보이는 요소 이름으로, AI 패널 대화는 요청·답변·도구 실행까지 같은 시간축에 기록합니다.
- 녹화는 `%APPDATA%\pluck-windows-native\recordings\<날짜-시간>`에 JPEG와 `meta.json`으로 저장되고 이 PC 밖으로 나가지 않습니다. 화면에 보인 계정 정보나 입력한 값도 그대로 남으니 공유하기 전에 확인하세요.
- 개요(`Ctrl+Shift+O`)의 **녹화** 탭에서 다시 봅니다. 재생/일시정지(Space), 5초 이동(←/→), 1~8배속, 빈 구간 건너뛰기, 단계 목록을 눌러 그 시점으로 이동할 수 있고, 화면 아래에 현재 단계가 자막으로 나옵니다.
- **동영상으로 저장**은 현재 배속과 빈 구간 설정대로 자막을 넣은 WebM을 만듭니다. 실제 재생 시간만큼 걸립니다.

### AI 패널

오른쪽 패널에서 Claude와 Codex에게 바로 작업을 맡길 수 있습니다. 앱이 이미 로그인된 로컬 CLI(`claude`, `codex`)를 헤드리스로 실행하므로 구독 중인 계정을 그대로 씁니다. 별도 API 키는 필요 없습니다. 두 CLI와 Node.js가 설치돼 있어야 합니다.

- AI는 기본적으로 `%APPDATA%\pluck-windows-native\assistant\claude` 또는 `assistant\codex` 작업 폴더에서 실행되고, 여기에 놓인 `agent.mjs`로 브라우저를 조작합니다. 브라우저 조작 지침은 시스템 프롬프트(Claude `--append-system-prompt`, Codex `developerInstructions`)로 전달하고, 대화마다 `runs\<대화>` 스크립트 폴더를 따로 씁니다. 부모 터미널·에이전트의 세션 환경 변수는 제거하고 기존 CLI 로그인 위치는 유지합니다.
- **실행 방식:** Claude는 `claude -p --input-format stream-json`으로 입력을 열어 둔 채 실행하고, Codex는 `codex app-server`(JSON-RPC)로 실행합니다. 둘 다 대화마다 프로세스가 따로 떠서 **여러 대화가 동시에** 일할 수 있습니다. 대화 목록에서 실행 중인 대화에는 `●`가 붙고, 다른 대화로 바꿔도 작업은 계속됩니다.
- **작업 중 추가 지시:** 실행 중에 보낸 메시지는 대기열에 쌓이지 않고 진행 중인 작업에 바로 들어갑니다(Claude는 stdin, Codex는 `turn/steer`). 전달할 수 없는 순간에만 대기열로 갑니다.
- **실시간 표시:** 답변 스트리밍, 생각 과정(Claude thinking, Codex 추론 요약), 명령 실행과 그 출력의 마지막 줄, 할 일 목록(Claude TodoWrite, Codex plan), 사용량을 보여 줍니다. 입력창 위 상태 줄에 지금 하는 일과 경과 시간이 나오고, 90초 넘게 새 소식이 없으면 빨갛게 알려 줍니다. 출력 파이프를 손자 프로세스가 붙잡아도 CLI가 끝나면 2초 안에 실행을 정리합니다.
- **CLI 옵션(대화별):** 모델(Claude는 opus·sonnet·haiku·fable 별칭, Codex는 `model/list`로 받은 목록), 추론 수준(Claude `--effort`, Codex `effort`), 권한, 작업 폴더를 고를 수 있습니다. 권한 기본값은 **제한**입니다. Claude는 `acceptEdits`에 `node`·읽기·웹 검색·TodoWrite만 허용하고, Codex는 작업 폴더 쓰기와 네트워크만 허용합니다. **계획만**(Claude `plan`), **읽기 전용**(Codex), **전체 권한**(Claude `--dangerously-skip-permissions`, Codex `dangerFullAccess`)을 고를 수 있고, 전체 권한은 경고를 띄우며 새 대화로 이어지지 않습니다. Claude 대화의 작업 폴더를 바꾸면 CLI 세션 대신 대화 기록으로 이어 갑니다.
- **슬래시 명령:** 입력창에서 `/`를 치면 `/new`, `/stop`, `/model`, `/effort`, `/mode`, `/folder`, `/compact` 메뉴가 나옵니다. 앞의 여섯 개는 패널이 처리하고, `/compact`와 목록에 없는 명령(스킬, 사용자 명령)은 CLI에 그대로 보냅니다.
- "현재 탭" 칩이 켜져 있으면 보고 있는 탭 정보가 함께 전달되어 "이 페이지 요약해줘" 같은 요청이 가능합니다. 이를 위해 `browser.activeTab()` API를 추가했습니다.
- 대화마다 브라우저 세션이 따로 있습니다. AI가 직접 연 탭은 사이드바에서 `Claude · 대화 제목` 그룹에 나타나고, 다른 대화나 터미널 에이전트는 이 탭을 보거나 조작할 수 없습니다. 이어받은 대화는 원래 대화의 세션을 그대로 씁니다. 새 대화 버튼은 이전 방을 보존합니다. 상단 대화 목록에서 이전 방을 고르면 내용과 CLI 세션이 복원되어 이어서 대화할 수 있습니다. 대화는 `assistant-history.json`에 저장하며 앱을 다시 열어도 남습니다. 실행 중에 새 대화를 열거나 다른 대화로 바꿔도 기존 작업은 계속됩니다.
- AI가 조작하는 동안 페이지 위에 다음이 표시됩니다. 실제 시스템 마우스는 움직이지 않습니다.
  - 화면 가장자리를 천천히 도는 오로라 테두리
  - 씨앗 마크와 반짝이는 문구가 있는 상태 알약. 작업이 끝나면 체크 표시와 민트색으로 바뀝니다.
  - 대상 요소로 모여드는 모서리 괄호와 동작 칩(클릭·입력·선택 등). 입력 중에는 깜빡이는 커서가 함께 나옵니다.
  - 살짝 넘치게 미끄러지는 pluck 커서와 클릭할 때의 이중 파동
  - 페이지를 읽을 때 위에서 아래로 한 번 지나가는 스캔 빛
  AI 패널에도 같은 씨앗 마크가 쓰입니다. 작업 상태 줄은 120ms 늦게 나타나 깜빡임을 막고, 문구가 반짝입니다. 실행 중인 도구 줄에는 빛이 지나가고, 브라우저 도구 줄에는 레일이 빛납니다. 대기 중에는 세 가지 색 점이 물결칩니다. 모든 동작은 `docs/ego-motion-reference.md` §3의 pluck 모션 값(시간 4단계, `--ease`·`--ease-lift`·`--ease-pop`)을 쓰고, 동작 줄이기 설정을 켜면 움직임 없이 투명도만 바뀝니다. `AI가 조작하는 탭 따라보기`는 AI 패널이 열려 있고 그 패널에 보이는 대화에만 적용됩니다. 다른 대화나 터미널 에이전트는 보고 있는 탭을 바꾸지 않습니다. 끄면 항상 현재 탭을 유지합니다. 임의 `js` 명령은 작업 상태만 표시하며 구체적인 클릭 위치를 추적하지 않습니다.
- 대화 저장은 600ms 단위로 묶어 백그라운드에서 처리하고 정상 종료 시 남은 내용을 저장합니다. 스트리밍 Markdown은 최대 초당 20회 갱신하며, 입력할 때 변경되지 않은 대화 목록과 아이콘을 다시 만들지 않습니다.
- 입력창의 사진 버튼, 붙여넣기, 드래그로 PNG/JPEG/GIF/WebP를 첨부할 수 있습니다. 한 번에 6장, 장당 5MB까지 지원하며 사진만 보내는 것도 가능합니다. 원본은 앱 데이터의 `assistant/images`에 복사하고 대화에는 미리보기를 저장합니다. Claude에는 이미지 콘텐츠 블록을, Codex에는 `--image` 인수를 전달합니다.
- 엔진을 바꾸면 `이 대화 이어받기`가 표시됩니다. 선택하면 실행 중이던 원래 작업을 중지하고, 메시지·미완성 답변·사진을 복사한 새 대화를 대상 엔진에 만듭니다. 기존 두 엔진의 방은 보존됩니다. 이어받은 뒤 요청을 입력하면 최근 대화와 사진을 전달하며, 전체 대화·도구 결과는 작업 폴더의 기록 파일에서 읽을 수 있습니다. 다른 엔진의 내부 세션이나 숨겨진 추론을 공유하는 기능은 아닙니다.
- 권한: Claude는 작업 폴더 안 파일 편집과 `node` 명령, 웹 검색만 자동 허용됩니다. Codex는 `workspace-write` 샌드박스에 로컬 네트워크를 허용해 실행합니다.

탭은 드래그로 순서를 바꿀 수 있고(재시작 후에도 유지), 가운데 클릭으로 닫을 수 있습니다. AI 작업 공간은 접을 수 있으며 에이전트가 명령을 실행 중이면 보라색 점이 표시됩니다.

## 에이전트 CLI

별도 PowerShell에서 다음을 실행합니다. 새 에이전트 탭은 현재 선택된 탭을 바꾸지 않습니다.

### 세션 격리

모든 명령은 하나의 세션 안에서 실행됩니다. AI 패널의 대화와 터미널 에이전트는 각자 세션을 가지며, 자기 세션의 탭만 조회·조작·닫기할 수 있습니다. 다른 세션의 탭 ID를 넘기면 `Tab belongs to another session`으로 거절합니다.

- 사람의 탭은 `activeTab()`으로 지금 보고 있는 탭을 읽은 세션만 조작할 수 있고, 닫을 수는 없습니다. 사람이 다른 세션의 탭을 보고 있으면 `activeTab()`은 실패합니다.
- 세션은 사람이 보는 탭을 바꾸지 않습니다. `show(id)`도 AI 패널에서 따라보는 대화일 때만 화면을 옮기고, 그 외에는 `shown: false`를 돌려줍니다.
- 세션 ID는 브라우저가 발급합니다. 이름(`label`)은 사이드바 표시용일 뿐이라 같은 이름을 써도 세션이 합쳐지지 않습니다.

```powershell
.\cli.ps1 session '조사 작업'      # 세션 발급, 출력된 session 값을 아래처럼 지정
$env:PLUCK_SESSION = 's1a2b3c4d5e6'
.\cli.ps1 list
.\cli.ps1 new https://example.com
.\cli.ps1 snapshot 2
.\cli.ps1 click 2 '@f0:1'
.\cli.ps1 fill 2 '@f0:2' 'hello'
.\cli.ps1 press 2 Enter
.\cli.ps1 scroll 2 down 600
.\cli.ps1 show 2
.\cli.ps1 screenshot 2 .\tab.png
.\cli.ps1 snapshot 2 diff                       # 이전 스냅샷 이후 바뀐 요소만
.\cli.ps1 hover 2 'text=메뉴'
.\cli.ps1 type 2 '추가 입력'
.\cli.ps1 select 2 '#size' 'L'
.\cli.ps1 press 2 'Control+A'
.\cli.ps1 clickAt 2 320 180
.\cli.ps1 back 2
.\cli.ps1 screenshot 2 .\marked.png annotate     # 참조 번호 상자를 그려서 캡처
```

### 입력 방식 (신뢰 이벤트)

클릭·입력·키 입력은 DevTools `Input` 도메인으로 보냅니다. 그래서 페이지에서 `event.isTrusted`가 `true`인 실제 입력으로 보이고, 사람이 누른 것과 똑같이 처리됩니다. 리치 에디터(블로그·문서 편집기)나 스크립트로 만든 이벤트를 거부하는 사이트에서도 동작합니다.

- `click`: 대상을 화면 가운데로 스크롤하고 마우스를 옮긴 뒤 클릭합니다. 마우스가 움직이면서 호버 메뉴가 닫히고 레이아웃이 바뀔 수 있으므로, 클릭 직전에 위치를 다시 잽니다. 다음 경우에는 `el.focus(); el.click()`으로 대신합니다.
  - 대상이 다른 요소에 가려져 있을 때
  - 대상이 다른 출처의 iframe 안에 있을 때
  - 실제 클릭이 대상에 닿지 않았을 때
  결과의 `input` 값(`trusted` 또는 `script`)으로 어느 방식이었는지 알 수 있습니다.
- `fill`: 대상에 포커스를 주고 내용을 전체 선택한 뒤 `Input.insertText`로 바꿉니다. contenteditable 편집기에서도 됩니다. 입력 후 값이 기대와 다르면 예전 방식(value setter + input/change 이벤트)으로 대신합니다.
- `type`은 기존 내용을 지우지 않고 현재 포커스 위치에 이어서 입력합니다.
- `press`는 이름 있는 키, F1~F12, 한 글자를 받고 `Control`/`Shift`/`Alt`/`Meta`와 조합할 수 있습니다.
- `scroll`은 화면 가운데에서 마우스 휠을 굴리므로 페이지 안쪽의 스크롤 영역도 움직입니다. 문서가 움직이지 않으면 `window.scrollBy`로 대신합니다.
- 대상은 스냅샷 참조(`@f0:3`), `text=보이는 이름`, CSS 선택자로 지정할 수 있습니다. `wait`에 `text=문구`를 주면 페이지에 그 글자가 나타날 때까지 기다립니다.

### 스냅샷

요소마다 다음 정보가 붙습니다.
- `role`: link, button, textbox, checkbox, combobox 등
- `text`: 접근성 이름. aria-label, aria-labelledby, label, 글자, alt, title, placeholder 순으로 찾은 첫 값입니다.
- `value`: 입력값. 비밀번호는 가립니다.
- `state`: checked, disabled, expanded, selected, required, focused
- `offscreen`: 화면 밖에 있는 요소에만 붙습니다.

화면에 보이는 요소가 먼저 나옵니다. h1~h3 목차(`headings`)와 스크롤 위치·문서 높이(`scroll`)도 함께 돌려줍니다.

`{ diff: true }`는 같은 탭·같은 주소에서 찍은 이전 스냅샷과 비교해 바뀐 요소만 돌려줍니다. 본문이 그대로면 `text`는 `null`입니다. 참조 번호는 스냅샷마다 새로 매겨지므로, 비교는 요소의 역할·이름·값·상태로 하고 포커스 변화는 무시합니다. 동작 후 결과를 확인할 때 쓰면 토큰을 크게 줄일 수 있습니다.

### MCP 서버

`mcp.mjs`는 별도 패키지 없이 동작하는 stdio MCP 서버입니다. pluck을 켤 때마다 `%APPDATA%\pluck-windows-native\mcp.mjs`에 최신본을 써 두므로, 이 경로를 MCP 클라이언트에 등록하면 됩니다. pluck은 다른 에이전트의 설정 파일을 직접 고치지 않습니다.

```powershell
claude mcp add pluck -- node "$env:APPDATA\pluck-windows-native\mcp.mjs"
codex mcp add pluck -- node "$env:APPDATA\pluck-windows-native\mcp.mjs"
```

MCP 서버 프로세스 하나가 세션 하나입니다. 사이드바에는 `MCP`라는 이름으로 나오고, `PLUCK_SESSION_LABEL`로 이름을 바꿀 수 있습니다. 서버가 종료되면 그 세션이 연 탭을 닫습니다.

제공하는 도구는 `tabs`, `open`, `active_tab`, `goto`, `snapshot`, `click`, `click_at`, `hover`, `fill`, `type`, `select`, `press`, `scroll`, `back`, `forward`, `wait`, `screenshot`, `js`, `show`, `close`입니다. `screenshot`은 PNG 이미지로 돌려주고 `annotate` 옵션을 지원합니다.

`snapshot`의 참조 번호는 페이지나 프레임이 바뀌면 새로 받아야 합니다. CSS 선택자도 지원합니다. 제어 API는 `127.0.0.1`에만 열리고 연결 토큰은 사용자 프로필에 저장됩니다.
기본 스냅샷은 본문 4,000자와 조작 요소 80개로 제한합니다. 전체 내용이 필요하면 `browser.snapshot(id, { full: true })` 또는 `.\cli.ps1 snapshot 2 full`을 사용하세요.

여러 단계의 자동화는 `agent.mjs`에 JavaScript를 한 번 전달해 실행합니다. 각 단계는 로컬 브라우저와 통신하고, 중간 판단은 스크립트 안에서 처리합니다.

```powershell
@'
const task = await browser.session('example research');
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

`node .\agent.mjs .\my-task.mjs`처럼 파일로 전달할 수도 있습니다. 먼저 `browser.session('작업 이름')`으로 세션을 엽니다. 반환된 `task`에는 `id`, `list`, `open`, `show`, `goto`, `snapshot`, `click`, `clickAt`, `hover`, `fill`, `type`, `select`, `press`, `scroll`, `back`, `forward`, `wait`, `js`, `screenshot`, `close`가 있습니다. 세션을 지정하지 않으면 실행마다 새 세션이 발급됩니다. `--keep-tabs`로 실행하면 발급된 세션 ID를 표준 오류에 출력하므로, 다음 실행에 `node .\agent.mjs --session <id> --keep-tabs .\step2.mjs`처럼 넘기면 같은 탭을 이어서 씁니다. `PLUCK_SESSION` 환경 변수로도 지정할 수 있고, AI 패널은 대화의 세션을 이 변수로 전달합니다. 예전 `browser.useOrCreateSpace(name)`은 `browser.session(name)`과 같습니다. Node.js는 이 자동화 스크립트 실행에만 사용하며 브라우저 앱 자체에는 필요하지 않습니다.

### 자동 탭 정리

- 기본적으로 스크립트가 성공하거나 오류로 끝나면 **해당 실행에서 새로 연 탭과 팝업**을 닫고 WebView2를 해제합니다. 같은 세션의 다른 실행이 연 탭과 사용자 탭은 건드리지 않습니다.
- 여러 번의 CLI 실행에 걸쳐 이어서 작업해야 할 때만 `node .\agent.mjs --keep-tabs .\my-task.mjs`를 사용합니다. 표준 입력에도 `--keep-tabs`를 쓸 수 있습니다. 이 옵션도 오류 종료 시에는 새로 연 탭을 정리합니다. 마지막 단계에서는 남겨 둔 탭에 `task.close(id)`를 호출하세요.
- 실행 중에는 30초마다 생존 신호를 보냅니다. 강제 종료나 기존 CLI 사용으로 남은 AI 탭은 **작업 공간 전체가 15분 동안 사용되지 않으면** 최대 1분 이내에 정리합니다. 명령 실행 중이거나 사용자가 현재 보고 있는 탭이 있는 공간은 제외합니다.
- AI 탭은 세션당 20개, 전체 60개까지만 열 수 있습니다. 한도에 도달하면 기존 탭을 닫으라는 오류를 반환합니다. 팝업도 같은 한도를 적용합니다.
- 앱 재시작 시 사용자 탭과 AI 작업 탭의 URL·작업 공간·선택 상태를 복원합니다. 로그인 프로필도 유지됩니다. 이전 실행의 탭 ID는 다시 조회해야 합니다.
- 초기화 실패 상태의 탭도 `close`로 정리할 수 있습니다.

작업 공간마다 탭과 페이지 입력 포커스·스크롤 상태가 따로 유지됩니다. 에이전트가 새 탭을 열어도 사용자가 보고 있는 탭은 바뀌지 않습니다. 모든 작업 공간은 현재 같은 WebView2 프로필을 사용하므로 로그인 쿠키와 사이트 저장소는 공유됩니다. 사이트 계정 자체까지 작업별로 분리하는 기능은 아직 없습니다.
앱을 닫으면 사용자 탭과 AI 작업 탭 URL을 저장하고 다음 실행 때 복원합니다. 페이지 안의 입력 중인 내용과 스크롤 위치는 재시작 후 복원되지 않습니다. AI가 이어서 작업할 때는 기존 작업 URL과 사이트 임시저장 상태부터 확인합니다.
사이트가 `window.open()`으로 여는 팝업은 같은 작업 공간의 새 탭에 연결됩니다. 팝업에서 `window.opener` 통신과 `window.close()`가 동작합니다.

## 현재 범위

AI 응답 중에 보낸 메시지는 진행 중인 작업에 바로 반영됩니다. CLI가 끝나 가는 순간처럼 전달할 수 없을 때만 대기 요청이 됩니다. 대기 요청은 대화별로 저장되며 사진과 전송 당시 탭 정보도 함께 유지됩니다. 정상 완료 후 순서대로 실행하고, 중지·오류·재시작 후에는 대기 목록의 실행 버튼으로 재개합니다. 각 대기 요청은 취소할 수 있으며 현재 작업 중지 버튼은 보내기와 별도로 표시됩니다.

AI 실행 상태와 선택한 대화를 저장합니다. 실행 도중 앱이 종료되면 다음 실행에서 해당 대화에 **중단된 작업 이어가기** 버튼이 나타납니다. 이어갈 때 현재 탭과 사이트의 임시저장 상태를 먼저 확인하도록 안내합니다. 실행 중인 AI가 있으면 자동화 API의 `shutdown`은 거절하며, 사용자가 창을 닫을 때는 확인합니다. 사용자 탭 중 마지막으로 보고 있던 탭도 복원합니다. 사이트에 저장되지 않은 편집 내용이나 실행 중인 프로세스 자체를 복구하지는 않습니다.

구현한 기능은 다음과 같습니다.

- 탭, 주소창, 검색, 뒤로/앞으로
- 프로필 공유와 에이전트 탭
- iframe을 포함한 접근성 스냅샷(diff 지원)
- 실제 입력 이벤트로 하는 클릭·입력·키·호버·휠
- 좌표 클릭과 번호 표시 스크린샷
- 대기와 JavaScript 실행
- MCP 서버 Chrome 프로필 이전, 확장 프로그램 관리, 공식 `pluck-browser` 스킬과의 호환은 아직 구현되지 않았습니다. WebView2는 Edge 렌더링 엔진을 사용합니다.
