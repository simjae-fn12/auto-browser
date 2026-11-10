# Oinzi for Linux

Windows 버전(`native/`, WinForms + WebView2)과 같은 브라우저의 리눅스 버전입니다. Electron에 들어 있는 오픈소스 Chromium을 쓰며 Microsoft Edge·WebView2와는 관계가 없습니다.

화면 UI(`native/chrome/chrome.html`), 에이전트 클라이언트(`native/agent.mjs`), MCP 서버(`native/mcp.mjs`), 조작 표시(`native/assistant/activity.js`)는 Windows 버전과 **같은 파일을 함께 씁니다**. 리눅스 쪽은 그 파일들을 띄우는 호스트(`main.cjs`)만 따로 둡니다.

## 설치와 실행

Node.js 20 이상이 필요합니다.

```bash
./linux/install.sh      # npm install, Chromium 샌드박스 설정(sudo), ~/.local/bin/oinzi와 앱 메뉴 등록
oinzi                   # 또는 앱 메뉴에서 Oinzi
```

설치 없이 개발용으로 띄우려면 `cd linux && npm install && npm start`를 실행합니다. Ubuntu 23.10 이상에서는 처음 한 번 Chromium 샌드박스 도우미에 권한을 줘야 합니다(`install.sh`가 처리).

```bash
sudo chown root:root linux/node_modules/electron/dist/chrome-sandbox
sudo chmod 4755 linux/node_modules/electron/dist/chrome-sandbox
```

## 데이터 위치

| 내용 | 위치 |
| --- | --- |
| 설정·탭·프로필·로그인 상태 | `~/.config/Oinzi` (`OINZI_DATA_DIR`로 바꿀 수 있음) |
| 에이전트 연결 정보 | `~/.config/Oinzi/connection.json` |
| MCP 서버 | `~/.config/Oinzi/mcp.mjs` (실행할 때마다 갱신) |
| 사용자 파일 | `~/문서/Oinzi` |

## 에이전트

`agent.mjs`, MCP 서버, 명령 목록은 Windows와 같습니다. 자세한 사용법은 [`native/README.md`](../native/README.md#에이전트-cli)를 보세요.

```bash
node ~/.config/Oinzi/agent.mjs script.js                    # 여러 동작을 한 번에
claude mcp add oinzi -- node ~/.config/Oinzi/mcp.mjs       # MCP 등록
./linux/cli.mjs session '조사 작업'                           # 명령 하나씩 (cli.ps1 대신)
export OINZI_SESSION=s1a2b3c4d5e6
./linux/cli.mjs new https://example.com
./linux/cli.mjs snapshot 2
```

## 지원 현황

| 기능 | 상태 |
| --- | --- |
| 사이드바·커맨드 팔레트·단축키·설정·세션 개요 | 지원 |
| 에이전트 CLI·`agent.mjs`·MCP (신뢰 입력, 스냅샷·diff, 스크린샷, 비교, batch) | 지원 (`tests/native-input.test.mjs` 통과) |
| 프로필(쿠키 분리) | 지원 |
| AI 패널 (Claude·Codex) | 다음 단계 |
| 사이트 계정 저장·`login`, 녹화, 예약 작업, 확장 프로그램, 다른 브라우저에서 가져오기 | 다음 단계 |

## 구현 메모

- 에이전트가 작업하는 백그라운드 탭은 창 왼쪽 위의 1×1 컨테이너 안에 실제 크기로 둡니다. Chromium은 창에 없는 뷰나 다른 뷰에 완전히 가려진 뷰를 그리지 않고 입력 응답도 멈추는데, 잘려서 보이는 뷰는 계속 그리기 때문입니다.
- 창에서 뺐다가 다시 넣은 뷰는 다시 그려지지 않으므로, 뷰는 꼭 필요할 때만 부모를 옮기고 그 뒤 `invalidate()`로 새로 그리게 합니다. 평소에는 `setVisible`로 보이고 숨깁니다.
- `alert`·`confirm`·`prompt`는 `page-preload.cjs`가 가로채 호스트에 묻습니다. 에이전트가 조작하는 탭은 자동으로 수락하고 다음 스냅샷의 `dialogs`로 알려 주며, 사람 탭은 기본 대화 상자를 띄웁니다.
