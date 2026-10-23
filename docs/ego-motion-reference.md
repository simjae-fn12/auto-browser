# ego lite 모션·그래픽 분석 (pluck UI 참고용)

- 대상: `C:\Program Files\Citro Labs\ego lite\Application\0.6.0.20` (Citro Labs, Chromium 기반)
- UI 소스: `local-ego-daemon\client` (Vite 빌드 SPA: JS 439개, CSS 4개)
- 분석 방법: REA `analyze_javascript_application`(정적 분석, 실행하지 않음) + CSS 토큰과 키프레임 추출
  - Evidence: `ev_e2995989edc26474e0b23d7b0ba091c826c8b3c5ca448fd4f7df41f16b5e15b3` (그래프 커버리지 partial)
- 원칙: 수치와 원리만 참고한다. 에셋, 코드, 클래스 이름, 문구는 가져오지 않는다.

## 1. 관찰한 것 (shipped CSS에 있는 값)

### 시간 단계 (duration 토큰, 16단계)
| 단계 | 1 | 2 | 3 | 4 |
|---|---|---|---|---|
| short | 50ms | 100ms | 150ms | 200ms |
| medium | 250ms | 300ms | 350ms | 400ms |
| long | 450ms | 500ms | 550ms | 600ms |
| very-long | 700ms | 800ms | 900ms | 1000ms |

기본 transition은 **150ms + cubic-bezier(.4,0,.2,1)**(Tailwind 기본값)이다. hover와 색 변화는 거의 모두 이 값을 쓴다.

### 이징 계열
| 계열 | 곡선 | 성격 |
|---|---|---|
| standard | `(.34,.69,.1,1)` | 빠르게 출발해 길게 감속. 패널·탭 전환의 기본 |
| smooth | `(.32,.87,.26,1)` | standard보다 부드러움 |
| snappy 1→4 | `(.36,.87,.27,1)` → `(.4,1.21,.24,1)` | 단계가 오를수록 끝에서 살짝 넘침(오버슈트) |
| bouncy 1→4 | `(.4,1.27,.19,1)` → `(.36,1.59,0,.95)` | 확실한 튕김. 작은 요소에만 사용 |
| expo-out | `(.16,1,.3,1)` | 도구 칩이 펼쳐질 때 280ms |

핵심 원리는 **같은 곡선 모양에 오버슈트 양만 단계별로 키운 계열**을 두는 것이다. 요소 크기와 중요도에 따라 단계를 고른다.

### AI 패널(채팅) 연출
- **생각 중 텍스트 반짝임**: 글자에 `linear-gradient(90deg, 흐린색 0%, 진한색 50%, 흐린색 100%)`를 `background-clip:text`로 입히고, `background-size:200%`를 1.5s linear로 무한히 흘린다. 스피너 대신 "생각 중…" 글자 자체가 빛난다.
- **생각 블록 등장**: `opacity 0→1` + `translateY(8px→0)` (fade-up)
- **도구 실행 칩 등장**: `opacity 0→1` + `scale(.97→1)`. 아주 작은 확대라서 튀지 않는다.
- **에이전트 연결 표시**: 150ms 지연 후 `visibility`만 켠다. 빠르게 끝나는 연결에서 깜빡임을 없애는 지연 표시 기법이다.
- **스피너**: 1s linear 회전. 원호 길이(dash)도 함께 변한다.

### 팝오버·메뉴
- 열릴 때 `fade-in` + `zoom-in-95`(scale .95→1) + `slide-in-from-*-2`(0.5rem 이동), 150ms
- 닫힐 때는 같은 값을 반대로 적용한다(exit 키프레임).

### 기타
- **검색 결과로 이동할 때 강조**: 강조색 18% 배경 → 투명으로 사라지는 펄스(`color-mix`)
- **홈 바**: JS 스프링(stiffness 100, damping 10) 기본값이 있다. 어디에 쓰이는지는 정적 분석으로 확인하지 못했다.
- `prefers-reduced-motion`이면 애니메이션을 끄는 규칙(`animation:none`)이 있다.

## 2. 추론 (확인되지 않음)
- 토큰 이름 체계(short/medium/long × 1-4, ease-standard)는 Material 3 모션 토큰과 같은 구조라 그 체계를 바탕으로 한 것으로 보인다.
- 앱을 실행해서 관찰하지는 않았다. 실제 어느 화면에서 어떤 토큰을 쓰는지는 CSS 선택자로 추정한 범위까지만 안다.

## 3. pluck에 적용할 제안 (pluck 자체 값)

> 적용 상태: 페이지 위 AI 오버레이(`native/assistant/activity.js`)와 AI 패널 작업 상태(`chrome.html`의 "AI working state" 블록)에 반영했다. 표현은 pluck 것으로 새로 만들었다. 씨앗 마크(꽃잎 세 개가 공전), 오로라 테두리, 모서리 괄호, 스캔 빛이 그것이고, ego의 에셋·코드·문구는 쓰지 않았다.

pluck은 지금 `--ease: cubic-bezier(.16,1,.3,1)` 하나만 쓴다. 이렇게 확장할 것을 제안한다.

```css
:root {
  /* 시간: 4단계로 단순화 */
  --t-instant: 90ms;   /* 누름, 체크 */
  --t-quick: 160ms;    /* hover, 메뉴 열기·닫기 */
  --t-move: 280ms;     /* 패널·탭 이동 */
  --t-scene: 480ms;    /* 개요 열기, 큰 전환 */
  /* 이징: 기본, 오버슈트 1단계, 오버슈트 2단계 */
  --ease: cubic-bezier(.16, 1, .3, 1);        /* 기존 유지 */
  --ease-lift: cubic-bezier(.35, 1.12, .3, 1); /* 메뉴·칩: 아주 살짝 넘침 */
  --ease-pop: cubic-bezier(.38, 1.35, .25, 1); /* 배지, 녹화 점 같은 작은 요소 */
}
```

| 대상 | 지금 | 제안 |
|---|---|---|
| 팝업 메뉴 | translateY 4px + scale .98, 140ms | scale .96→1 + 메뉴 버튼 방향으로 6px, `--t-quick` `--ease-lift`. 닫힐 때도 반대 애니메이션 |
| 작업 상태 줄(`#aiLive`) | 회전 스피너 | 글자에 그라데이션 반짝임(2s, 흐림→잉크→흐림), 스피너는 작게 유지 |
| 도구 실행 줄 | 바로 나타남 | `opacity` + `scale(.98→1)`, `--t-quick` |
| 새 답변 블록 | 바로 나타남 | fade-up 6px, `--t-move` |
| 짧은 상태 표시 | 바로 표시 | 120ms 뒤에 표시. 빨리 끝나는 작업에서 깜빡이지 않게 |
| 녹화 중 점 | 고정 | `--ease-pop` 맥박 |
| 모든 연출 | reduced-motion 처리 없음 | `@media (prefers-reduced-motion: reduce)`에서 이동 없이 투명도만 바꿈 |
