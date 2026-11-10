# Oinzi for Linux

Windows 버전(`native/`, WinForms + WebView2)과 같은 브라우저의 리눅스 버전입니다. Electron에 들어 있는 오픈소스 Chromium을 쓰며 Microsoft Edge·WebView2와는 관계가 없습니다.

화면 UI(`native/chrome/chrome.html`), 에이전트 클라이언트(`native/agent.mjs`), MCP 서버(`native/mcp.mjs`), 조작 표시(`native/assistant/activity.js`)는 Windows 버전과 **같은 파일을 함께 씁니다**. 리눅스 쪽은 그 파일들을 띄우는 호스트(`main.cjs`)만 따로 둡니다.

## 설치와 실행

설치 파일(.deb)을 만들어 설치하는 방법이 기본입니다. 빌드에는 Node.js 20 이상이 필요하고, 설치한 앱은 Node 없이 단독으로 실행됩니다.

```bash
cd linux && npm install && npm run dist          # linux/dist/oinzi_<버전>_amd64.deb
sudo apt install ./dist/oinzi_*_amd64.deb         # /opt/Oinzi, /usr/bin/oinzi, 앱 메뉴, AppArmor 프로필
oinzi                                             # 또는 앱 메뉴에서 Oinzi
```

패키지는 Ubuntu 24.04 이상에서 Chromium 샌드박스가 동작하도록 AppArmor 프로필(`/etc/apparmor.d/oinzi`)을 함께 설치합니다. 지우려면 `sudo apt remove oinzi`를 실행합니다(설정과 프로필 데이터는 `~/.config/Oinzi`에 남습니다).

개발할 때는 체크아웃에서 바로 띄웁니다. Ubuntu 23.10 이상이면 처음 한 번 샌드박스 도우미에 권한을 줘야 합니다.

```bash
cd linux && npm install && npm start
sudo chown root:root node_modules/electron/dist/chrome-sandbox && sudo chmod 4755 node_modules/electron/dist/chrome-sandbox
```

`install.sh`는 이 과정을 대신하고 체크아웃을 가리키는 `~/.local/bin/oinzi`를 만듭니다. .deb와 함께 쓰면 PATH 순서상 이쪽이 먼저 실행되니 둘 중 하나만 쓰세요.

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
| 탭 잠자기(메모리 절약), 쉬는 에이전트 탭 스로틀링 | 지원 (리눅스 전용) |
| AI 패널 (Claude·Codex) | 다음 단계 |
| 사이트 계정 저장·`login`, 녹화, 예약 작업, 확장 프로그램, 다른 브라우저에서 가져오기 | 다음 단계 |

## 오래 써도 가볍게

- **탭 잠자기**: 사람이 `TabSleepMinutes`(기본 30분) 동안 보지 않은 탭과 에이전트가 그동안 건드리지 않은 탭은 페이지를 내려놓고 주소·제목·방문 기록만 남깁니다. 사이드바에서는 흐리게 보이고, 누르거나 에이전트가 명령을 보내면 그 자리에서 다시 불러옵니다(뒤로 가기 기록 유지). 소리가 나는 탭과 사람이 입력하던 탭은 재우지 않습니다.
- **지연 복원**: 시작할 때 저장된 탭은 활성 탭만 불러오고 나머지는 잠든 상태로 복원합니다.
- **쉬는 에이전트 탭**: 에이전트가 20초 동안 명령을 보내지 않으면 그 탭을 숨기고 일반 백그라운드 탭처럼 타이머를 줄입니다. 다음 명령이 오면 바로 다시 깨웁니다.
- 끄려면 `~/.config/Oinzi/settings.json`에 `"TabSleepMinutes": 0`을 넣습니다.

## 구현 메모

- 에이전트가 작업하는 백그라운드 탭은 창 왼쪽 위의 1×1 컨테이너 안에 실제 크기로 둡니다. Chromium은 창에 없는 뷰나 다른 뷰에 완전히 가려진 뷰를 그리지 않고 입력 응답도 멈추는데, 잘려서 보이는 뷰는 계속 그리기 때문입니다.
- 창에서 뺐다가 다시 넣은 뷰는 다시 그려지지 않으므로, 뷰는 꼭 필요할 때만 부모를 옮기고 그 뒤 `invalidate()`로 새로 그리게 합니다. 평소에는 `setVisible`로 보이고 숨깁니다.
- `alert`·`confirm`·`prompt`는 `page-preload.cjs`가 가로채 호스트에 묻습니다. 에이전트가 조작하는 탭은 자동으로 수락하고 다음 스냅샷의 `dialogs`로 알려 주며, 사람 탭은 기본 대화 상자를 띄웁니다.
