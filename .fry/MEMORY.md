# Native: 스크립트 대화 상자(alert·confirm·beforeunload) 자동 처리

## WHY: WebView2 기본 대화 상자를 전부 끔
- 네이티브 "사이트에서 나갈까요?"·alert 창은 누가 클릭할 때까지 페이지와 `ExecuteScriptAsync`/`goto`를 막는다. 에이전트는 이 창을 볼 수도 누를 수도 없어 작업이 멈췄다.
- `AreDefaultScriptDialogsEnabled = false`는 탭 생성 시 모든 탭에 건다. 이 설정은 다음 탐색부터 적용되므로 실행 중에 켜고 끄는 방식은 쓸 수 없다.
- `ScriptDialogOpening`은 기본 대화 상자를 끈 경우에만 발생한다.

## 규칙
- 에이전트 탭이거나, 에이전트 명령이 실행 중인(`PendingCommands > 0`) 사람 탭이면 자동으로 수락한다. prompt에는 기본값을 넣는다.
- 자동 수락한 내용은 `tab.Dialogs`에 쌓았다가 다음 `snapshot`의 `dialogs` 필드로 넘기고 비운다. 에이전트가 alert 문구를 알 수 있게 하려는 것이다.
- 그 밖의 사람 탭은 WinForms MessageBox나 입력 폼으로 직접 묻는다. WebView2 이벤트 안에서 모달을 띄우면 재진입 문제가 생기므로 deferral과 `BeginInvoke`로 이벤트가 끝난 뒤에 띄운다.

# Chrome UI: AI 패널 설정 접기, 전송·중지 버튼

- 사용자 선호: 패널은 Aside 브라우저처럼 단순하게 둔다. 헤더에는 대화 목록 select(제목 역할)와 아이콘 3개만 둔다. 5시간/주간 사용량 막대는 사용자가 좋아하므로 헤더 바로 아래에 항상 보인다.
- 입력창 아래 `.afoot` 줄: 왼쪽에 Claude/Codex 엔진 전환, 오른쪽에 `#aiOpts` 요약 칩("모델 · 추론 · 권한")을 둔다. 칩을 누르면 입력창 위에 `.aopts` 서랍(모델·추론·권한·폴더·따라보기)이 열린다. 전체 권한이면 칩 글자가 빨간색이 된다.
- `.send` 클래스는 `margin-left:auto`를 쓴다. 중지 버튼과 전송 버튼이 함께 보이면 auto 여백이 둘로 나뉘어 중지 버튼이 가운데로 간다. 그래서 `#aiStop + #aiSend { margin-left: 0 }`가 필요하다.
- 함정: 이미 포커스가 있는 chromeView에 `Focus()`를 다시 걸면 chrome 문서에 잠깐 blur가 생긴다. 팔레트는 window blur가 오면 닫히므로, 마우스로 새 탭을 누를 때마다 바로 닫혔다. 그래서 `frozen`에서는 `ContainsFocus`가 아닐 때만 Focus를 건다.

# 테스트: 실행 중인 Oinzi를 건드리지 않고 검증
- `OINZI_DATA_DIR=<임시폴더>`로 Debug 빌드를 따로 띄우고, `OINZI_CONNECTION=<임시폴더>/connection.json`을 지정해 `node agent.mjs <스크립트>`를 실행한다.
- 테스트 스크립트는 `const b = await browser.session("이름")`으로 받은 `b.open/goto/js/snapshot`을 쓴다. `browser.open`은 없다.
- `goto`에 `data:` URL을 넣으면 "Navigation failed"가 난다. 로컬 http 서버를 띄워서 테스트한다.

# 사용자 파일 폴더: 문서\Oinzi (녹화·AI 작업·스크린샷)

## WHY: 보이는 결과물과 숨길 데이터를 분리
- `문서\Oinzi\{녹화, AI 작업, 스크린샷}`은 시작할 때 만든다. Oinzi는 설치 프로그램이 없는 단일 exe라 "설치 시" 단계가 없다. 이 폴더에는 사람이 직접 열어 볼 결과물만 둔다.
- 프로필(로그인), 대화 기록, 녹화 원본 프레임은 `%APPDATA%\Oinzi`에 남긴다. 문서 폴더는 OneDrive로 동기화될 수 있고, 녹화 원본에는 화면에 보인 계정 정보가 그대로 담기기 때문이다.
- 녹화는 원본이 JPEG 프레임과 `meta.json`이고, 개요(Ctrl+Shift+O) → 녹화 탭에서 재생한다. "동영상으로 저장"은 WebM을 만들며 기본 저장 위치가 `문서\Oinzi\녹화`다.

## 함정: AI 기본 작업 폴더를 바꾸면 Claude 세션이 이어지지 않음
- `chat.Cwd`가 null이면 기본 작업 폴더로 `AI 작업`을 쓴다. agent.mjs와 `runs\`는 여전히 내부 workspace에 있다. Claude에는 `--add-dir`, Codex에는 `writableRoots`로 그 경로를 열어 준다.
- Claude CLI 세션은 폴더 단위로 저장된다. 그래서 `Conversation.SessionCwd`에 세션을 만든 폴더를 기록한다. null이면 예전 기본값(workspace)으로 본다. 실행할 때의 cwd와 다르면 Session을 비우고 `NeedsContext`로 대화 기록에서 이어 간다.

# 녹화 → MP4 (ffmpeg): BrowserRecordingVideo.cs

## 흐름
- 녹화 원본은 0.25초 간격으로 찍은 JPEG 프레임이다. 화면이 바뀌지 않은 프레임은 저장하지 않는다.
- 녹화를 멈추면 `EncodeAndPostAsync`가 ffmpeg로 `문서\Oinzi\녹화\<날짜 시간> <제목>.mp4`를 만든다. 플레이어의 "MP4로 저장" 버튼(`rec.video` 메시지)도 같은 경로를 쓰고, 버튼으로 만들었을 때는 탐색기에서 파일을 선택해 보여 준다.
- 앱을 종료할 때(`StopAllRecordings`)는 인코딩하지 않는다. ffmpeg가 끝나기 전에 앱이 꺼지면 파일을 옮기지 못하기 때문이다. 이런 녹화는 나중에 버튼으로 만든다.
- ffmpeg가 없으면 예전 방식(캔버스 + MediaRecorder로 WebM 만들기, 실제 재생 시간만큼 걸림)으로 돌아간다.

## 함정
- 각 프레임이 화면에 머무는 시간은 최대 2초로 줄인다(`MaxFrameSeconds`). 자막 시간도 같은 방식(`Squeeze`)으로 줄여야 영상과 맞는다.
- subtitles 필터에 Windows 경로(`C:`)를 넣으면 이스케이프 문제가 생긴다. 그래서 ffmpeg의 WorkingDirectory를 녹화 폴더로 두고 상대 경로를 쓴다.
- 한글 자막은 `force_style='FontName=Malgun Gothic'`이 있어야 제대로 나온다.
- concat 목록의 마지막 파일은 한 번 더 적어야 마지막 프레임의 duration이 적용된다.

# AI 패널 하단 메뉴 (Aside 참고, 그대로 베끼지 않음)
- 사용자 선호: 설정은 서랍이나 select 대신 팝업 메뉴로 보여 준다. 입력창 왼쪽 `+`(사진 첨부·현재 탭·작업 폴더), 하단 왼쪽 권한 🛡·폴더 📁, 하단 오른쪽 모델(엔진 선택 포함, 모델이 많으면 검색)과 추론(따라보기 스위치 포함)이다. 저작권 문제가 없도록 문구·아이콘·색은 Oinzi 것으로 쓴다.
- 숨겨진 `.aset` 안의 select와 버튼(`#aiModel/#aiEffort/#aiMode/#aiFolder/#aiFolderClear/#aiFollow`)이 실제 값을 가진다. 메뉴는 이 값을 읽고 바꾼 뒤 `change` 이벤트를 보내 기존 `ai.setting` 전송 로직을 그대로 쓴다. `.seg`(엔진 전환)도 숨긴 채 남겨 두고 모델 메뉴에서 클릭한다.
- `[hidden]`은 `display:flex` 같은 CSS에 덮이므로, 숨길 수 있는 요소에는 `X[hidden]{display:none}`이 필요하다.

# 내장 스킬 (native/skills → .claude/skills)
- `native/skills/<이름>/SKILL.md`는 EmbeddedResource(`skills/<이름>\SKILL.md`)로 넣는다. 실행할 때 내부 workspace와 `문서\Oinzi\AI 작업`의 `.claude\skills`에 써 둔다. 사용자가 고른 다른 폴더와 `~/.claude`에는 쓰지 않는다(사용자 선택: "Oinzi 안에만").
- Codex는 `.claude/skills`를 자동으로 읽지 않으므로 INSTRUCTIONS.md의 `{{SKILLS}}` 경로 안내로 SKILL.md를 읽게 한다. 제한 모드의 Claude 허용 도구에 `Skill`을 추가했다.
- 블로그 스킬의 핵심 주의점: Oinzi는 beforeunload를 자동으로 확인 처리하므로 페이지를 벗어나기 전에 반드시 임시저장해야 한다. 발행은 사람이 허락했을 때만 한다.


# 에이전트 입력: DevTools 신뢰 이벤트 (BrowserInput.cs)

## WHY: JS el.click() 대신 CDP Input
- 스크립트로 만든 이벤트는 `isTrusted=false`라서 리치 에디터나 봇 차단 사이트가 무시한다. ego와 Aside는 모두 CDP Input을 쓴다(`D:\ego-analysis\comparison.md`).
- click 흐름: 대상 스크롤 → 중심점 계산 → `elementFromPoint`로 가려졌는지 확인 → mouseMoved → **위치를 다시 잼** → pressed/released → 프로브 확인.
  - 프로브는 "대상에 닿은 trusted click"만 인정한다. 신호가 없으면 `el.focus(); el.click()`으로 대신한다.
  - 페이지 이동 때문에 프로브를 읽을 수 없으면 클릭이 성공한 것으로 본다.
- fill: 내용 전체 선택 후 `Input.insertText`. 값이 다르면 value setter로 대신한다. 결과의 `input` 필드가 trusted인지 script인지 알려 준다.

## 함정
- CDP 인자에 `null` 필드가 있으면 "Value does not fall within the expected range"가 난다. `CdpJson`(WhenWritingNull)으로 직렬화한다.
- `ExecuteScriptAsync`는 스크립트 예외를 `null`로 삼킨다. 메인 프레임은 `ExecuteScriptWithResultAsync`로 원인 메시지를 살린다. iframe(CoreWebView2Frame)에는 이 API가 없다.
- 주입 스크립트에서 최상위 `const`를 쓰지 않는다. 같은 문서에 두 번째로 주입하면 재선언 에러가 난다. 헬퍼(`LocateScript`)는 IIFE 안에 넣는다.
- hover 메뉴처럼 마우스가 움직이면 레이아웃이 바뀌는 경우가 있다. 그래서 mouseMoved 뒤에 좌표를 다시 잰다.
- 다른 출처의 iframe은 프레임 위치(offset)를 알 수 없으므로 script 방식으로 대신한다.
- `Read(body, x)`는 JSON bool을 "True"로 돌려준다. bool 인자는 `Flag()`로 읽는다.

# 스냅샷 diff, MCP 서버
- 참조 번호는 스냅샷마다 새로 매겨진다. 그래서 diff는 (프레임, role, 이름, href, value, state에서 focused를 뺀 값)을 키로 비교한다. 주소가 바뀌면 비교하지 않고 전체를 돌려준다.
- `mcp.mjs`는 의존성이 없다. 시작할 때 `%APPDATA%\Oinzi\mcp.mjs`에 써 둔다. 다른 에이전트 설정은 자동으로 고치지 않는다. ego가 Codex 규칙과 `~/.claude/skills`를 몰래 바꾸는 것을 반면교사로 삼은 결정이다. 프로세스 하나가 세션 하나이고, 종료할 때 cleanup-run을 한다.

# AI 작업 오버레이(activity.js)와 패널 모션
- Oinzi 고유 디자인이다(ego는 수치와 원리만 참고). 씨앗 마크, 오로라 테두리, 모서리 괄호와 동작 칩, Oinzi 커서와 이중 파동, 읽기 스캔으로 구성된다. 색은 `--ai #7462ee`, 민트, 꽃잎 분홍이다.
- `__oinziActivity(label, target, done, command)`에 네 번째 인자로 명령을 넘겨 동작 칩과 연출을 고른다. `version` 값이 다르면 기존 페이지의 오버레이를 교체한다.
- 함정:
  - shadow root 안의 `@property`는 등록되지 않는다. 회전하는 conic 레이어를 ring mask 안에 넣어 해결했다.
  - 백그라운드(숨은) 탭에서는 `requestAnimationFrame`이 실행되지 않는다. 등장 애니메이션은 `void el.offsetWidth`로 강제 reflow한 뒤 클래스를 붙인다.
  - 숨은 탭의 `Page.captureScreenshot`은 새 프레임을 기다리다 수십 초 멈출 수 있다. 2.5초 안에 끝나지 않으면 `captureBeyondViewport` + 화면 영역 clip으로 다시 찍어 렌더링을 강제한다.
- 테스트: `node --test tests/native-input.test.mjs`. Debug 빌드가 필요하고, 임시 데이터 폴더로 따로 띄운다.

# AI 계정 여러 개: CLAUDE_CONFIG_DIR / CODEX_HOME 폴더 분리 (AssistantAccounts.cs)

## WHY: 자격 증명 파일 바꿔 끼우기를 버림
- 사용자는 원래 `~/.claude/.credentials.json`·`~/.codex/auth.json`을 `auth-simjae.json`처럼 복사해 두고 바꿔 썼다. 갱신 토큰(refresh token)은 쓸 때마다 바뀌므로 저장해 둔 사본이 금방 쓸 수 없게 된다. 같은 토큰을 두 곳에 두면 한쪽이 갱신하면서 다른 쪽이 무효가 된다.
- 그래서 계정마다 설정 폴더를 따로 두고 실행할 때 환경 변수로 가리킨다. 토큰은 폴더 안에서 갱신된다. 재시작할 필요가 없고, 대화마다 다른 계정으로 동시에 실행할 수도 있다. Aside도 `~/.aside/u/0`, `u/1`처럼 계정별 폴더를 쓴다.
- **로그인 파일을 다른 위치로 복사하는 테스트는 금지**(같은 이유). 테스트는 로그인하지 않은 빈 계정으로 하고, "Not logged in"/401이 나오면 폴더 분리가 동작하는 것이다.

## 규칙·함정
- `default` 계정은 사용자의 기본 로그인이다. 이 계정에는 환경 변수를 넣지 않는다(사용자가 터미널에 설정한 값을 그대로 물려받음).
- 공유 설정은 한 방향으로만 가져온다. 파일(settings.json, CLAUDE.md / config.toml, AGENTS.md)은 복사하고, 폴더(skills, agents, commands, plugins)는 `mklink /J` 정션으로 연결한다. 정션은 관리자 권한이 필요 없다. `~/.claude.json`에서는 `mcpServers`만 복사한다. 사용자의 원본 설정은 절대 쓰지 않는다.
- 계정을 삭제할 때는 정션을 먼저 따로 지운다(`DeleteAccountDir`). 그래야 원래 skills 폴더가 지워지지 않는다.
- CLI 세션은 계정 폴더에 저장된다. 그래서 `Conversation.SessionAccount`가 지금 계정과 다르면 Session을 비우고 `NeedsContext`로 이어 간다. SessionCwd와 같은 방식이다. 예전에는 `HistorySnapshot`에 SessionCwd가 빠져 있어서 재시작할 때마다 Claude 세션이 끊겼는데, 지금은 함께 저장한다.
- 플러그인 매니페스트(`installed_plugins.json`, `known_marketplaces.json`)에는 절대 경로가 들어 있고 Claude가 자기 설정 폴더 기준으로 다시 쓴다. 그래서 `plugins` 폴더는 정션으로 통째로 연결하지 않는다. 계정 폴더에 실제 폴더를 만들고, json은 복사하고, 하위 폴더만 정션으로 연결한다.
- 자격 증명 가져오기는 기본 계정의 실제 로그인 파일과, 다른 계정과 refresh token이 같은 파일을 거절한다. 같은 토큰을 두 곳에 두면 한쪽이 로그아웃된다.
- 실패한 실행도 CLI의 session 이벤트를 먼저 보낸다. 로그인 전 실행이 그렇다. 그래서 그 실행이 새로 만든 세션은 실패하면 지운다(`AssistantRun.NewSession`, `ForgetFailedSession`). 지우지 않으면 다음 메시지마다 "No conversation found"가 난다. 이 오류가 stderr로 와도 `PostAssistant`에서 세션을 비운다.
- `claude auth status --json`은 loggedIn·email·subscriptionType을 준다. Codex는 `auth.json`의 id_token(JWT) 클레임에 email과 `https://api.openai.com/auth`.chatgpt_plan_type이 있다.
- Codex가 npm으로 설치돼 있으면 PATH에는 `codex.cmd`만 있다. `FindTool`은 npm 패키지 안에 있는 `codex.exe` 경로도 찾아본다.

# 설정 페이지 (BrowserSettings.cs, chrome.html "Settings (Ctrl+,)")
- 개요와 같은 방식으로 뷰포트를 얼리고 녹이는 오버레이다(`paletteOpen` 공유). 팔레트·개요·설정은 서로를 dismiss하고, 닫히는 타이머는 `st.open`도 확인해야 thaw가 엇갈리지 않는다.
- 테마는 `CoreWebView2Profile.PreferredColorScheme`으로 바꾼다. chrome 페이지도 같은 프로필을 쓰므로 `prefers-color-scheme`이 바뀌고, `reportTheme`가 창 제목 표시줄 색까지 맞춘다.
- Prefs는 C#에서 PascalCase로 저장하고 JS에는 `PrefsMessage()`로 camelCase를 보낸다. 불린은 JS boolean으로 보낸다. `Read()`가 "True"/"False" 문자열로 돌려준다.
- 테스트: `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333`로 테스트 인스턴스를 띄우고 CDP로 chrome 페이지에 붙는다. `chrome.webview.postMessage`로 실제 메시지를 보낸다(`.omc/itest/cdp.mjs`, `restart.ps1`). `restart.ps1`은 pids.txt의 PID를 죽이므로 오래된 PID는 먼저 비운다.

# 프로필·사이트 계정 금고 (BrowserProfiles.cs)

## WHY / 설계
- 사용자는 Chrome 프로필을 수십 개 쓰고 자동화할 때 사이트마다 계정이 많다. 그래서 프로필(쿠키 분리)과 프로필별 계정 금고를 둘 다 만들었다. 사용자가 이 방식을 골랐다.
- AI는 비밀번호를 절대 받지 않는다. `login(tabId, credential)`을 부르면 Oinzi가 직접 입력한다. 입력하기 전에 두 가지를 확인한다. 로그인 칸이 있는 문서의 `location.hostname`이 저장된 사이트와 같은지(또는 하위 도메인인지), 탭 프로필이 계정 프로필과 같은지다. 프롬프트 인젝션으로 다른 사이트에 비밀번호를 입력하게 만드는 것을 막기 위해서다.
- 프로필 = 작업공간(사용자 선택, 외주 프로젝트별 계정 분리 용도). 사이드바 「열린 탭」·Ctrl+Tab·Ctrl+1~9는 `userProfile`의 탭만 다룬다(`InWorkspace`). AI 세션 탭은 프로필과 무관하게 모두 보인다. `userProfile`은 사람이 보는 탭을 따라간다(`SelectedIndexChanged` → `FollowWorkspace`). 그래서 다른 프로필 탭이 선택되면 작업공간도 바뀐다. 프로필을 고르면(`prof.use`) 그 프로필에서 마지막으로 본 탭이 열리고, 탭이 없으면 홈 탭을 하나 만든다. 로그인 유지는 WebView2 프로필별 쿠키가 담당한다.
- AI 세션마다 프로필이 하나씩 있다(`AgentSession.Profile`). 이유: 여러 세션이 서로 다른 계정으로 동시에 일하게 하려고(예: 터미널은 Oinzi 계정, 패널은 caryoung 계정). `open`에 profile이 없으면 세션 프로필을 쓴다. 새 세션은 만들 때의 `userProfile`을 쓰고, 터미널은 `--profile`이나 `OINZI_PROFILE`로 정한다(`session` 명령의 profile). 패널 대화는 처음 실행할 때 `Conversation.Profile`로 고정된다. 패널 자식 프로세스에서는 `OINZI_PROFILE`을 지운다. 지우지 않으면 사용자 터미널 값이 대화 프로필을 덮어쓴다. tabs.json의 sessions에도 profile을 저장한다.
- 세션 개요(Ctrl+Shift+O)는 사람 탭을 프로필마다 카드 하나로 나눈다(space `user:<프로필id>`, 누르면 `SwitchWorkspace`). 녹화는 그대로 `user` 하나다(지금 보는 탭을 녹화한다).
- Chrome에서 가져온 것: 프로필 이름과 구글 이메일만 가져와 메모에 `구글: 이메일`로 적었다. 쿠키와 비밀번호는 Chrome 앱 전용 암호화를 우회해야 해서 가져오지 않는다. 비밀번호는 사용자가 직접 내보낸 CSV로만 가져온다(`cred.import` → `ImportPasswordCsv`). 파일 선택 창은 C#에서 띄우고, CSV 내용은 chrome 페이지로 보내지 않는다. 가져온 뒤에는 CSV를 지울지 묻는다.
- 테스트 함정: 이 PC는 한글 IME가 켜져 있어서 SendKeys로 경로를 입력하면 한글 자모로 들어간다. 파일 선택 창 자동화는 포기하고 로직만 리플렉션으로 검증했다.
- chrome 페이지에는 비밀번호를 보내지 않는다(hasPassword만). 수정할 때 비밀번호를 비워 두면 기존 값을 그대로 둔다.

## 함정
- WebView2 프로필은 `CoreWebView2ControllerOptions.ProfileName`(`p-<id>`)이다. 기본 프로필은 options 없이 만든다. 팝업(NewWindowRequested)은 반드시 연 탭과 같은 프로필이어야 붙는다.
- 테마와 다운로드 폴더는 CoreWebView2Profile마다 설정된다. 그래서 탭을 만들 때마다 `ApplyProfile`을 부른다. 인터넷 사용 기록 삭제도 열린 모든 프로필에 적용한다.
- 프로필을 삭제할 때 폴더는 브라우저가 잡고 있어서 바로 못 지운다. `PendingDelete`에 넣어 두고 다음에 시작할 때 `EBWebView\p-<id>`를 지운다.
- 보안 리뷰 후 다시 설계했다. 로그인 칸 찾기, 포커스, 확인은 모두 CDP `Page.createIsolatedWorld` + `Runtime.evaluate(contextId)`로 한다. 페이지 세계에서 돌리면 페이지가 JSON.stringify나 querySelectorAll을 바꿔치기해 다른 사이트인 척할 수 있었다. 사이트 주소는 `core.Source`의 IdnHost(최상위)로만 확인한다. iframe 로그인은 지원하지 않는다. 입력하기 직전에 loaderId, 주소, 포커스를 다시 확인하고, 스크립트로 값을 넣는 대체 방법은 쓰지 않는다. 스크립트에는 비밀번호를 넣지 않고 길이만 비교한다.
- 문서 단위로 잠근다(`DocumentSerial`은 NavigationStarting마다 +1). 로그인을 채운 문서에서는 js, screenshot, `[value` 선택자를 거절하고 스냅샷에서 비밀번호를 가린다. js가 실행된 문서에서는 login을 거절한다(입력 이벤트를 미리 걸어 두는 공격을 막기 위해서).
- 한계: DPAPI는 같은 Windows 사용자의 프로세스라면 풀 수 있다. 전체 권한 AI는 profiles.json을 직접 풀 수 있다. 화면에도 이렇게 적어 두었다.
- 백그라운드 탭에서 `press('Enter')`로 폼이 제출되지 않는다. 이 기능을 넣기 전부터 있던 문제다. 로그인 제출은 버튼을 클릭한다.
- 사이드바 팝업 메뉴는 사이드바 폭 안에 있어야 한다. 오른쪽은 네이티브 WebView가 덮고 있어서 넘치면 가려진다. `openMenu({ sidebar: true })`.

# 예약 작업 (BrowserSchedules.cs) · 요소 고르기 (BrowserPicker.cs) · 에이전트 추가 명령 (BrowserAgentExtras.cs)

## 규칙·함정
- 예약 실행은 `new Conversation`을 직접 만든다. `NewConversation()`은 그 대화를 선택해 버려서, 사용자가 패널에서 보던 대화가 바뀐다. 실행이 끝나는 시점은 `PostAssistant`의 "done"에서 `FinishScheduleRun`으로 잡는다. `scheduleRuns`(대화 id → 예약)로 짝을 짓는다.
- 다음 실행 시각은 `NextRun(LastRun ?? Created)`이다. 예약을 수정하거나 켜면 LastRun을 지금으로 바꾼다. 그래야 바꾼 시각이 "놓친 실행"으로 처리돼 곧바로 실행되지 않는다. 시작할 때 놓친 실행 확인은 대화 기록이 로드된 뒤(5초 뒤)에 한다.
- 요소 고르기는 CDP `Runtime.evaluate(awaitPromise)`로 페이지 안의 Promise가 끝날 때까지 기다린다(클릭하거나 Esc, 최대 2분). 페이지 클릭은 capture 단계에서 막아서 실제로 눌리지 않는다.
- `compare`는 이미지를 base64로 주고받는다. Oinzi가 에이전트의 경로를 대신 읽거나 쓰면 Claude 제한 모드의 샌드박스를 우회하게 되기 때문이다. GDI+ Bitmap은 스트림이 닫히면 깨지므로 픽셀을 복사해 둔다.
- 계정 사용량 바로 조회(OAuth usage API)는 넣지 않았다. 로그인 토큰을 비공개 엔드포인트로 보내야 해서다. 다른 브라우저 쿠키 가져오기도 하지 않는다. Chrome·Edge의 v20 앱 전용 암호화는 다른 프로그램이 읽지 못하게 하려는 보호 장치다.

# 시작하기: Chrome·Edge 프로필 가져오기 · 기본 브라우저 · 링크 넘겨받기 (BrowserSetup.cs)

## WHY / 설계
- 다른 브라우저에서 읽는 건 `Local State`의 `profile.info_cache`(프로필 이름·계정 이메일)뿐. 쿠키·비밀번호는 v20 앱 바인딩 암호화라 우회하지 않는다 → 비밀번호는 「비밀번호 내보내기」(그 브라우저 프로필로 `--profile-directory=… chrome://password-manager/settings` 실행) → 받은 CSV를 「CSV 가져오기」.
- `BrowserProfile.Source = "chrome:Profile 3"`. 같은 이메일은 Chrome·Edge 어디서 왔든 프로필 하나(노트에 이메일이 있으면 가져온 것으로 침). Source 이전에 스크립트로 가져온 프로필은 `LinkBrowserProfiles()`가 시작할 때 이메일로 Source를 채운다.
- 기본 브라우저는 Windows가 사용자만 바꿀 수 있게 막아 둠 → HKCU에 후보로 등록(`StartMenuInternet\Oinzi`, `RegisteredApplications`, ProgId `OinziURL`/`OinziHTML`) 후 `ms-settings:defaultapps?registeredAppUser=Oinzi`를 연다. 현재 기본 여부는 `UrlAssociations\https\UserChoice` ProgId로 판단.
- 첫 실행(`prefs.SetupOffered == false`)에 할 일이 있으면 설정 「시작하기」를 한 번 자동으로 연다.

## 흐름: 한 데이터 폴더에 Oinzi 하나
- `Program.Main`: 데이터 폴더 경로 해시로 만든 Mutex `Local\oinzi-<hash>`. 이미 있으면 같은 이름 파이프로 링크(JSON 배열)를 넘기고 종료. 넘기기에 실패하면(닫히는 중) Mutex를 15초 기다렸다가 직접 실행.
- 받는 쪽 `ListenForLinks`는 초기화 초반에 시작하고, 탭 복원 전 도착한 링크는 `LaunchLinks`에 쌓았다가 복원 뒤 연다.
- 함정: 테스트 인스턴스(OINZI_DATA_DIR)는 다른 해시라 실제 Oinzi와 안 부딪힌다. 등록된 exe 경로는 그 파일이 없어졌을 때만 다시 쓴다(디버그 빌드가 기본 브라우저 등록을 가로채지 않게). node의 execFileSync로 env를 넘기면 다른 데이터 폴더로 떠서 넘기기 테스트가 틀어졌음 → PowerShell Start-Process로 테스트.

# 탭 전환 포커스: 페이지 첫 링크(「바로가기」)에 포커스 링 (PageView)
- 함정: TabControl 탭을 바꾸면 WinForms가 새 페이지 첫 컨트롤을 Tab 이동처럼 `Select(directed: true)` → WebView2가 `_lastMoveFocusReason = Next`로 기억했다가 페이지 안에 Tab을 넣음 → 네이버 「상단영역 바로가기」 같은 skip link가 :focus-visible로 뜬다.
- 해결: 탭 WebView는 `PageView`(WebView2 하위 클래스)로 만들고, 실제 Tab 키가 눌려 있을 때만 directed로 넘긴다. 탭 WebView를 새로 만들 때 `new WebView2` 대신 `PageView`를 써야 한다.

# 크롬 확장 프로그램 (BrowserExtensions.cs): WebView2 browser extensions
## WHY / 설계
- `CoreWebView2EnvironmentOptions.AreBrowserExtensionsEnabled = true`. 확장은 WebView2 프로필 단위 → Oinzi 프로필(작업공간)마다 따로. 설치 API는 `CoreWebView2Profile`에만 있어서, 열린 탭이 없는 프로필은 숨긴 컨트롤러를 잠깐 만들어 쓴다(`WithProfileAsync`).
- WebView2는 압축 풀린 폴더를 계속 참조 → `dataDir\extensions\<id>\<version>`에 복사본 유지. 크롬 웹 스토어 버튼·툴바·팝업이 없으므로 Oinzi가 직접: 스토어 CRX를 clients2.google.com에서 받아 CRX3 헤더(protobuf)에서 공개키를 꺼내 manifest `key`로 넣는다(→ 스토어와 같은 ID 유지). 사이드바 주소창 아래 `#exts` 버튼, 팝업은 테두리 없는 Form + WebView2(같은 프로필)로 `chrome-extension://<id>/<popup>`.
- 크롬에서 가져오기: `<User Data>\<프로필>\Extensions\<id>\<ver>`(이미 key 포함). `Secure Preferences`/`Preferences`의 extensions.settings로 기본 설치(was_installed_by_default)·내장(location 5,10)·꺼둔 것(disable_reasons) 제외. 테마·앱 제외.
## 함정
- `_metadata` 등 `_`로 시작하는 폴더가 있으면 압축 해제 확장 로드가 거부됨 → 복사할 때 `_locales` 빼고 삭제.
- WebView2 기본 내장 확장(Edge PDF Viewer, Clipboard)이 목록에 섞여 나옴 → Oinzi 저장소에 폴더가 있는 것만 보여 준다.
- 팝업 창을 Opacity로 숨기면(레이어드 창) WebView가 안 그려짐 → 화면 밖(-32000)에 띄웠다가 크기를 잰 뒤 옮긴다.
- 팝업은 Chrome의 "현재 탭" 개념이 없어 chrome.tabs로 현재 페이지를 읽는 확장(예: Dark Reader 팝업의 사이트 표시)은 페이지를 모른다. 페이지에 주입되는 content script는 정상 동작.

# 배포: 설치 파일(Inno Setup) · AI 도구 설치 (native/installer, BrowserSetup.cs)
- `native/installer/build.ps1` → self-contained publish(`publish-installer`) → ISCC → `native/dist/oinzi-setup-<ver>.exe`(약 35MB). 버전은 native.csproj `<Version>`. Inno Setup 6 필요(`winget install JRSoftware.InnoSetup`, 사용자 범위 설치면 `%LOCALAPPDATA%\Programs\Inno Setup 6`).
- 사용자별 설치(관리자 권한 X) `%LOCALAPPDATA%\Programs\Oinzi`. 데이터는 `%APPDATA%\Oinzi` 그대로라 제거해도 남는다. 제거 마지막에 `oinzi.exe --unregister`가 기본 브라우저 등록·시작 프로그램 항목을 지운다(Mutex보다 먼저 처리).
- 서명 안 된 exe라 SmartScreen "알 수 없는 게시자" 경고가 뜬다 → 없애려면 코드 서명 인증서.
- 「시작하기」 AI 연결: Claude는 공식 `irm https://claude.ai/install.ps1 | iex`, Codex는 npm(`@openai/codex`, npm 없으면 winget으로 Node LTS 먼저), Node는 winget. 보이는 PowerShell 창에서 실행해 진행·오류를 사람이 보게 한다. 상태는 창이 다시 활성화될 때 재검사.

# 이름 변경 pluck → Oinzi (Rebrand.cs), 로고
- 첫 실행 마이그레이션: `%APPDATA%\pluck-windows-native` → `%APPDATA%\Oinzi`로 옮기고 옛 자리에 정션을 남긴다(이미 등록된 `claude mcp add` 경로, WebView2 확장 경로가 계속 동작). JSON에 저장된 절대 경로 치환, Claude `projects\<경로 인코딩>` 폴더 이름 변경(기본 ~/.claude + 계정별 CLAUDE_CONFIG_DIR), 옛 이름 스킬 폴더 삭제, 시작 프로그램·기본 브라우저 등록 이전.
- 함정: 문서 폴더(`Documents\pluck`)는 터미널·편집기가 안에서 작업 중이면 이동이 실패한다 → 데이터 이동과 따로, 새 폴더가 비어 있는 동안 시작할 때마다 다시 시도한다.
- 함정: 비밀번호 금고 DPAPI 엔트로피 `"pluck-site-vault-v1"`은 바꾸면 저장된 비밀번호를 못 푼다 → 이름 변경에서 제외(일부러 남김).
- 환경 변수는 OINZI_*. 데이터 폴더만 옛 `PLUCK_NATIVE_DATA_DIR`도 받는다(테스트 스크립트 호환).
- 로고: `native/oinzi.svg`(보라 그라데이션 타일 + 흰 O 링 + AI 반짝이)가 원본. `oinzi.ico`는 이 SVG를 Edge headless로 1024px 렌더 → Pillow로 16~256 멀티 사이즈 ICO.

# 다른 PC 설치에서 겪은 문제 (Codex daemon, Claude PATH, 기본 계정)
- Codex 0.16x는 기본으로 공유 백그라운드 서버(daemon)를 통해 돈다. 다른 프로그램의 서버(Codex 데스크톱 앱, 관리자 권한으로 띄운 것)면 "액세스가 거부되었습니다 (os error 5)". → Oinzi가 띄우는 codex(app-server, login)는 `--help`에 `--no-daemon`이 있으면 붙인다(`AddCodexDaemonFlag`).
- Claude 공식 설치 스크립트는 `~\.local\bin`을 PATH에 넣지 않을 때가 있어 터미널에서 `claude`가 안 잡힌다(Oinzi는 FindTool이 그 폴더를 직접 봐서 동작). 「시작하기」 설치는 사용자 PATH에 추가까지 한다.
- 「기본 계정」= 그 Windows 사용자 폴더(~/.claude, ~/.codex)에 이미 있던 로그인. 설치 파일에는 계정 데이터가 없다. 로그아웃 상태에서 예전 이메일이 남아 보이던 문제 → 로그인 아님이면 이메일·요금제 표시를 지운다.
- Codex 기기 코드가 링크보다 먼저(또는 같은 줄에) 나오면 놓치던 문제 → 순서와 상관없이 잡는다.
