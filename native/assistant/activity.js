(() => {
  if (window.__oinziActivity?.version === 2) return;
  document.getElementById('__oinzi_activity')?.remove();
  const host = document.createElement('div');
  host.id = '__oinzi_activity';
  host.style.cssText = 'position:fixed;inset:0;z-index:2147483647;pointer-events:none';
  const shadow = host.attachShadow({ mode: 'closed' });
  // Oinzi's own motion set (docs/ego-motion-reference.md §3): four durations, one base curve and two overshoot steps.
  shadow.innerHTML = `<style>
    :host { --t-instant: 90ms; --t-quick: 160ms; --t-move: 280ms; --t-scene: 480ms;
      --ease: cubic-bezier(.16,1,.3,1); --ease-lift: cubic-bezier(.35,1.12,.3,1); --ease-pop: cubic-bezier(.38,1.35,.25,1);
      --violet: #7462ee; --lilac: #c4b5fd; --mint: #5eead4; --ink: #17131f; }
    * { box-sizing: border-box }

    /* Edge: a slow aurora band traced around the viewport, quiet enough to read through. */
    .edge { position: fixed; inset: 0; opacity: 0; transition: opacity var(--t-scene) var(--ease) }
    .on .edge { opacity: 1 }
    /* @property does not register inside a shadow root, so the band turns as a rotating layer behind a ring mask. */
    .ring-mask { position: absolute; inset: 0; padding: 3px; border-radius: 12px; overflow: hidden; opacity: .85;
      -webkit-mask: linear-gradient(#000 0 0) content-box, linear-gradient(#000 0 0); -webkit-mask-composite: xor; mask-composite: exclude }
    .ring-mask::before { content: ''; position: absolute; left: 50%; top: 50%; width: 300vmax; height: 300vmax; margin: -150vmax 0 0 -150vmax;
      background: conic-gradient(var(--violet), var(--mint) 25%, var(--lilac) 45%, var(--violet) 60%, #f0abfc 80%, var(--violet));
      animation: spin 6s linear infinite }
    .edge::after { content: ''; position: absolute; inset: 0; border-radius: 12px;
      box-shadow: inset 0 0 28px 2px color-mix(in srgb, var(--violet) 26%, transparent), inset 0 0 90px color-mix(in srgb, var(--mint) 8%, transparent) }
    .done .ring-mask::before { background: var(--mint); animation: none }

    /* Status pill: frosted, with the Oinzi seed mark and a label that shimmers while work is in progress. */
    .pill { position: fixed; top: 14px; left: 50%; display: flex; align-items: center; gap: 10px; height: 38px; padding: 0 16px 0 12px;
      border-radius: 19px; color: #f5f3ff; font: 500 13px/1 "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif; white-space: nowrap;
      background: color-mix(in srgb, var(--ink) 88%, transparent); backdrop-filter: blur(14px) saturate(1.4);
      box-shadow: 0 0 0 1px rgb(255 255 255 / .09), 0 10px 30px -8px rgb(23 19 31 / .55), 0 0 24px -6px color-mix(in srgb, var(--violet) 70%, transparent);
      transform: translate(-50%, -10px) scale(.96); opacity: 0;
      transition: transform var(--t-move) var(--ease-lift), opacity var(--t-quick) var(--ease), width var(--t-move) var(--ease), background var(--t-move) var(--ease) }
    .on .pill { transform: translate(-50%, 0) scale(1); opacity: 1 }
    .leaving .pill { transform: translate(-50%, -8px) scale(.98); opacity: 0 }
    .done .pill { background: color-mix(in srgb, #0f2b28 82%, transparent) }
    .label { display: inline-block; background: linear-gradient(90deg, #a79bc4 0%, #fff 45%, #fff 55%, #a79bc4 100%) 0 0 / 220% 100%;
      -webkit-background-clip: text; background-clip: text; color: transparent; animation: shimmer 2s linear infinite }
    .done .label { animation: none; background: none; color: #ccfbf1 }
    .label.swap { animation: shimmer 2s linear infinite, swap var(--t-quick) var(--ease) }
    .done .label.swap { animation: swap var(--t-quick) var(--ease) }

    /* Seed: three petals orbiting a core — Oinzi's mark. It folds into a check when the work is done. */
    .seed { position: relative; width: 18px; height: 18px; flex: none }
    .seed i { position: absolute; left: 6px; top: 6px; width: 6px; height: 6px; border-radius: 50% 50% 50% 0;
      background: linear-gradient(135deg, var(--lilac), var(--violet)); transform-origin: 3px 3px; animation: orbit 1.6s var(--ease) infinite }
    .seed i:nth-child(2) { animation-delay: -.53s; background: linear-gradient(135deg, #a5f3fc, var(--mint)) }
    .seed i:nth-child(3) { animation-delay: -1.06s; background: linear-gradient(135deg, #fbcfe8, #f0abfc) }
    .seed svg { position: absolute; inset: 0; opacity: 0; transform: scale(.6) }
    .seed path { stroke-dasharray: 16; stroke-dashoffset: 16 }
    .done .seed i { animation: none; transform: scale(0); opacity: 0; transition: transform var(--t-quick) var(--ease), opacity var(--t-quick) }
    .done .seed svg { opacity: 1; transform: scale(1); transition: transform var(--t-move) var(--ease-pop), opacity var(--t-instant) }
    .done .seed path { stroke-dashoffset: 0; transition: stroke-dashoffset var(--t-move) var(--ease) .06s }

    /* Target: four corner brackets that close in on the element, plus a verb chip. */
    .target { position: fixed; left: 0; top: 0; opacity: 0; transition: opacity var(--t-quick) var(--ease) }
    .target.show { opacity: 1 }
    .target .fill { position: absolute; inset: 0; border-radius: 8px; background: color-mix(in srgb, var(--violet) 9%, transparent) }
    .target b { position: absolute; width: 12px; height: 12px; border: 2.5px solid var(--violet); transition: transform var(--t-move) var(--ease-lift) }
    .target b:nth-of-type(1) { left: -4px; top: -4px; border-right: 0; border-bottom: 0; border-radius: 7px 0 0 0; transform: translate(-10px, -10px) }
    .target b:nth-of-type(2) { right: -4px; top: -4px; border-left: 0; border-bottom: 0; border-radius: 0 7px 0 0; transform: translate(10px, -10px) }
    .target b:nth-of-type(3) { left: -4px; bottom: -4px; border-right: 0; border-top: 0; border-radius: 0 0 0 7px; transform: translate(-10px, 10px) }
    .target b:nth-of-type(4) { right: -4px; bottom: -4px; border-left: 0; border-top: 0; border-radius: 0 0 7px 0; transform: translate(10px, 10px) }
    .target.show b { transform: none }
    .target .verb { position: absolute; left: -4px; top: -30px; padding: 4px 8px; border-radius: 7px; background: var(--violet); color: #fff;
      font: 600 11px/1 "Segoe UI", system-ui, sans-serif; letter-spacing: .02em; box-shadow: 0 4px 12px -4px var(--violet);
      transform: translateY(4px) scale(.94); opacity: 0; transition: transform var(--t-move) var(--ease-pop) .05s, opacity var(--t-quick) .05s }
    .target.show .verb { transform: none; opacity: 1 }
    .target.below .verb { top: auto; bottom: -30px }
    .typing .fill::after { content: ''; position: absolute; left: 10px; top: 25%; width: 2px; height: 50%; border-radius: 1px; background: var(--violet); animation: caret .9s steps(1) infinite }

    /* Cursor: the Oinzi pointer glides with a slight overshoot and presses with a double ripple. */
    .cursor { position: fixed; left: 0; top: 0; opacity: 0; transform: translate(var(--x), var(--y));
      transition: transform var(--t-scene) var(--ease-lift), opacity var(--t-quick) }
    .cursor.show { opacity: 1 }
    .cursor svg { display: block; filter: drop-shadow(0 3px 6px rgb(23 19 31 / .35)); transform-origin: 3px 3px; transition: transform var(--t-instant) var(--ease) }
    .cursor.press svg { transform: scale(.82) }
    .ring { position: absolute; left: -20px; top: -20px; width: 44px; height: 44px; border-radius: 50%; border: 2px solid var(--violet); opacity: 0 }
    .cursor.press .ring { animation: ripple 620ms var(--ease) }
    .cursor.press .ring + .ring { animation-delay: 120ms; border-color: var(--mint) }

    /* Reading: a soft scan line sweeps down the viewport once per snapshot. */
    .scan { position: fixed; left: 0; right: 0; top: 0; height: 120px; opacity: 0;
      background: linear-gradient(180deg, transparent, color-mix(in srgb, var(--violet) 10%, transparent) 70%, color-mix(in srgb, var(--mint) 35%, transparent) 99%, transparent) }
    .scan.go { animation: scan 1.1s var(--ease) }

    @keyframes spin { to { transform: rotate(360deg) } }
    @keyframes shimmer { from { background-position: 110% 0 } to { background-position: -110% 0 } }
    @keyframes swap { from { opacity: 0; transform: translateY(4px) } }
    @keyframes orbit { from { transform: rotate(0deg) translate(5px) rotate(0deg) } to { transform: rotate(360deg) translate(5px) rotate(-360deg) } }
    @keyframes ripple { from { transform: scale(.25); opacity: .9 } to { transform: scale(1.35); opacity: 0 } }
    @keyframes scan { 0% { transform: translateY(-120px); opacity: 0 } 15% { opacity: 1 } 85% { opacity: 1 } 100% { transform: translateY(100vh); opacity: 0 } }
    @keyframes caret { 50% { opacity: 0 } }
    @media (prefers-reduced-motion: reduce) {
      *, *::before, *::after { animation: none !important; transition: opacity var(--t-quick) !important }
      .label { color: #fff; background: none }
    }
  </style>
  <div class="stage">
    <div class="edge"><div class="ring-mask"></div></div>
    <div class="scan"></div>
    <div class="target"><div class="fill"></div><b></b><b></b><b></b><b></b><span class="verb"></span></div>
    <div class="cursor"><div class="ring"></div><div class="ring"></div>
      <svg width="26" height="30" viewBox="0 0 26 30"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#a78bfa"/><stop offset="1" stop-color="#6d28d9"/></linearGradient></defs>
      <path d="M3.5 2.6c0-.9 1-1.4 1.7-.8l17.2 14.1c.8.6.3 1.9-.7 1.9h-7.6c-.4 0-.8.2-1 .5l-4.7 6.9c-.6.8-1.9.4-1.9-.6V2.6z" fill="url(#g)" stroke="#fff" stroke-width="2" stroke-linejoin="round"/></svg></div>
    <div class="pill"><span class="seed"><i></i><i></i><i></i><svg viewBox="0 0 18 18"><path d="M4.5 9.5l3 3 6-7" fill="none" stroke="#5eead4" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"/></svg></span><span class="label"></span></div>
  </div>`;
  const $ = s => shadow.querySelector(s);
  const stage = $('.stage'), pill = $('.pill'), label = $('.label'), target = $('.target'), cursor = $('.cursor'), scan = $('.scan');
  const verbs = { click: '클릭', fill: '입력', type: '입력', select: '선택', hover: '가리키기', press: '키 입력', scroll: '스크롤', wait: '기다리는 중', goto: '이동', snapshot: '읽는 중' };
  // The pointer enters from the lower right the first time, then travels from wherever it last was.
  let at = { x: innerWidth - 60, y: innerHeight - 60 }, hideTimer, lastLabel = '';
  cursor.style.setProperty('--x', at.x + 'px'); cursor.style.setProperty('--y', at.y + 'px');

  const restart = (el, cls) => { el.classList.remove(cls); void el.offsetWidth; el.classList.add(cls); };
  const setLabel = (text) => {
    if (text === lastLabel) return;
    lastLabel = text;
    // Animate the pill's width so the label change reads as one object resizing, not a jump.
    const from = pill.getBoundingClientRect().width;
    label.textContent = text;
    pill.style.width = 'auto';
    const to = pill.getBoundingClientRect().width;
    if (from > 0 && Math.abs(from - to) > 1) { pill.style.width = from + 'px'; void pill.offsetWidth; pill.style.width = to + 'px'; }
    restart(label, 'swap');
  };
  const locate = (t) => t === ':focus' ? document.activeElement
    : t.startsWith('@') ? document.querySelector('[data-oinzi-ref="' + CSS.escape(t.slice(1)) + '"]')
    : t.startsWith('text=') ? null : document.querySelector(t);

  const api = (text, selector, done = false, command = '') => {
    clearTimeout(hideTimer);
    if (!host.isConnected) document.documentElement.append(host);
    stage.classList.remove('leaving');
    stage.classList.toggle('done', done);
    // A forced style flush (not requestAnimationFrame, which never fires in a background tab) lets the entrance transition run.
    if (!stage.classList.contains('on')) { void stage.offsetWidth; stage.classList.add('on'); }
    setLabel(text);
    if (command === 'snapshot' && !done) restart(scan, 'go');
    let el = null;
    try { el = selector ? locate(selector) : null; } catch { el = null; }
    if (el && el !== document.body && el !== document.documentElement) {
      el.scrollIntoView({ block: 'center', behavior: 'instant' });
      const r = el.getBoundingClientRect();
      target.style.cssText = `left:${r.left}px;top:${r.top}px;width:${r.width}px;height:${r.height}px`;
      target.classList.toggle('below', r.top < 40);
      target.classList.toggle('typing', (command === 'fill' || command === 'type') && !done);
      $('.verb').textContent = verbs[command] || '조작';
      if (!done) { target.classList.remove('show'); void target.offsetWidth; target.classList.add('show'); }
      at = { x: r.left + Math.min(r.width / 2, 40), y: r.top + r.height / 2 };
      cursor.classList.add('show');
      cursor.style.setProperty('--x', at.x + 'px'); cursor.style.setProperty('--y', at.y + 'px');
      if (done && command === 'click') restart(cursor, 'press');
    } else if (!done && command !== 'press' && command !== 'type') {
      target.classList.remove('show');
    }
    if (done) {
      target.classList.remove('typing');
      hideTimer = setTimeout(() => {
        stage.classList.add('leaving'); target.classList.remove('show'); cursor.classList.remove('show');
        hideTimer = setTimeout(() => { stage.classList.remove('on', 'leaving', 'done'); host.remove(); lastLabel = ''; pill.style.width = ''; }, 320);
      }, 900);
    } else hideTimer = setTimeout(() => api(lastLabel, '', true), 35000);
    return true;
  };
  api.version = 2;
  window.__oinziActivity = api;
})();
