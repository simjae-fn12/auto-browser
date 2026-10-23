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

# 테스트: 실행 중인 pluck을 건드리지 않고 검증
- `PLUCK_NATIVE_DATA_DIR=<임시폴더>`로 Debug 빌드를 따로 띄우고, `PLUCK_NATIVE_CONNECTION=<임시폴더>/connection.json`을 지정해 `node agent.mjs <스크립트>`를 실행한다.
- 테스트 스크립트는 `const b = await browser.session("이름")`으로 받은 `b.open/goto/js/snapshot`을 쓴다. `browser.open`은 없다.
- `goto`에 `data:` URL을 넣으면 "Navigation failed"가 난다. 로컬 http 서버를 띄워서 테스트한다.

# 사용자 파일 폴더: 문서\pluck (녹화·AI 작업·스크린샷)

## WHY: 보이는 결과물과 숨길 데이터를 분리
- `문서\pluck\{녹화, AI 작업, 스크린샷}`은 시작할 때 만든다. pluck은 설치 프로그램이 없는 단일 exe라 "설치 시" 단계가 없다. 이 폴더에는 사람이 직접 열어 볼 결과물만 둔다.
- 프로필(로그인), 대화 기록, 녹화 원본 프레임은 `%APPDATA%\pluck-windows-native`에 남긴다. 문서 폴더는 OneDrive로 동기화될 수 있고, 녹화 원본에는 화면에 보인 계정 정보가 그대로 담기기 때문이다.
- 녹화는 원본이 JPEG 프레임과 `meta.json`이고, 개요(Ctrl+Shift+O) → 녹화 탭에서 재생한다. "동영상으로 저장"은 WebM을 만들며 기본 저장 위치가 `문서\pluck\녹화`다.

## 함정: AI 기본 작업 폴더를 바꾸면 Claude 세션이 이어지지 않음
- `chat.Cwd`가 null이면 기본 작업 폴더로 `AI 작업`을 쓴다. agent.mjs와 `runs\`는 여전히 내부 workspace에 있다. Claude에는 `--add-dir`, Codex에는 `writableRoots`로 그 경로를 열어 준다.
- Claude CLI 세션은 폴더 단위로 저장된다. 그래서 `Conversation.SessionCwd`에 세션을 만든 폴더를 기록한다. null이면 예전 기본값(workspace)으로 본다. 실행할 때의 cwd와 다르면 Session을 비우고 `NeedsContext`로 대화 기록에서 이어 간다.

# 녹화 → MP4 (ffmpeg): BrowserRecordingVideo.cs

## 흐름
- 녹화 원본은 0.25초 간격으로 찍은 JPEG 프레임이다. 화면이 바뀌지 않은 프레임은 저장하지 않는다.
- 녹화를 멈추면 `EncodeAndPostAsync`가 ffmpeg로 `문서\pluck\녹화\<날짜 시간> <제목>.mp4`를 만든다. 플레이어의 "MP4로 저장" 버튼(`rec.video` 메시지)도 같은 경로를 쓰고, 버튼으로 만들었을 때는 탐색기에서 파일을 선택해 보여 준다.
- 앱을 종료할 때(`StopAllRecordings`)는 인코딩하지 않는다. ffmpeg가 끝나기 전에 앱이 꺼지면 파일을 옮기지 못하기 때문이다. 이런 녹화는 나중에 버튼으로 만든다.
- ffmpeg가 없으면 예전 방식(캔버스 + MediaRecorder로 WebM 만들기, 실제 재생 시간만큼 걸림)으로 돌아간다.

## 함정
- 각 프레임이 화면에 머무는 시간은 최대 2초로 줄인다(`MaxFrameSeconds`). 자막 시간도 같은 방식(`Squeeze`)으로 줄여야 영상과 맞는다.
- subtitles 필터에 Windows 경로(`C:`)를 넣으면 이스케이프 문제가 생긴다. 그래서 ffmpeg의 WorkingDirectory를 녹화 폴더로 두고 상대 경로를 쓴다.
- 한글 자막은 `force_style='FontName=Malgun Gothic'`이 있어야 제대로 나온다.
- concat 목록의 마지막 파일은 한 번 더 적어야 마지막 프레임의 duration이 적용된다.

# AI 패널 하단 메뉴 (Aside 참고, 그대로 베끼지 않음)
- 사용자 선호: 설정은 서랍이나 select 대신 팝업 메뉴로 보여 준다. 입력창 왼쪽 `+`(사진 첨부·현재 탭·작업 폴더), 하단 왼쪽 권한 🛡·폴더 📁, 하단 오른쪽 모델(엔진 선택 포함, 모델이 많으면 검색)과 추론(따라보기 스위치 포함)이다. 저작권 문제가 없도록 문구·아이콘·색은 pluck 것으로 쓴다.
- 숨겨진 `.aset` 안의 select와 버튼(`#aiModel/#aiEffort/#aiMode/#aiFolder/#aiFolderClear/#aiFollow`)이 실제 값을 가진다. 메뉴는 이 값을 읽고 바꾼 뒤 `change` 이벤트를 보내 기존 `ai.setting` 전송 로직을 그대로 쓴다. `.seg`(엔진 전환)도 숨긴 채 남겨 두고 모델 메뉴에서 클릭한다.
- `[hidden]`은 `display:flex` 같은 CSS에 덮이므로, 숨길 수 있는 요소에는 `X[hidden]{display:none}`이 필요하다.

# 내장 스킬 (native/skills → .claude/skills)
- `native/skills/<이름>/SKILL.md`는 EmbeddedResource(`skills/<이름>\SKILL.md`)로 넣는다. 실행할 때 내부 workspace와 `문서\pluck\AI 작업`의 `.claude\skills`에 써 둔다. 사용자가 고른 다른 폴더와 `~/.claude`에는 쓰지 않는다(사용자 선택: "pluck 안에만").
- Codex는 `.claude/skills`를 자동으로 읽지 않으므로 INSTRUCTIONS.md의 `{{SKILLS}}` 경로 안내로 SKILL.md를 읽게 한다. 제한 모드의 Claude 허용 도구에 `Skill`을 추가했다.
- 블로그 스킬의 핵심 주의점: pluck은 beforeunload를 자동으로 확인 처리하므로 페이지를 벗어나기 전에 반드시 임시저장해야 한다. 발행은 사람이 허락했을 때만 한다.


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
- `mcp.mjs`는 의존성이 없다. 시작할 때 `%APPDATA%\pluck-windows-native\mcp.mjs`에 써 둔다. 다른 에이전트 설정은 자동으로 고치지 않는다. ego가 Codex 규칙과 `~/.claude/skills`를 몰래 바꾸는 것을 반면교사로 삼은 결정이다. 프로세스 하나가 세션 하나이고, 종료할 때 cleanup-run을 한다.

# AI 작업 오버레이(activity.js)와 패널 모션
- pluck 고유 디자인이다(ego는 수치와 원리만 참고). 씨앗 마크, 오로라 테두리, 모서리 괄호와 동작 칩, pluck 커서와 이중 파동, 읽기 스캔으로 구성된다. 색은 `--ai #7462ee`, 민트, 꽃잎 분홍이다.
- `__pluckActivity(label, target, done, command)`에 네 번째 인자로 명령을 넘겨 동작 칩과 연출을 고른다. `version` 값이 다르면 기존 페이지의 오버레이를 교체한다.
- 함정:
  - shadow root 안의 `@property`는 등록되지 않는다. 회전하는 conic 레이어를 ring mask 안에 넣어 해결했다.
  - 백그라운드(숨은) 탭에서는 `requestAnimationFrame`이 실행되지 않는다. 등장 애니메이션은 `void el.offsetWidth`로 강제 reflow한 뒤 클래스를 붙인다.
  - 숨은 탭의 `Page.captureScreenshot`은 새 프레임을 기다리다 수십 초 멈출 수 있다. 2.5초 안에 끝나지 않으면 `captureBeyondViewport` + 화면 영역 clip으로 다시 찍어 렌더링을 강제한다.
- 테스트: `node --test tests/native-input.test.mjs`. Debug 빌드가 필요하고, 임시 데이터 폴더로 따로 띄운다.
