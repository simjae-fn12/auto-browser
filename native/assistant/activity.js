(() => {
  if (window.__pluckActivity) return;
  const host = document.createElement('div');
  host.style.cssText = 'position:fixed;inset:0;z-index:2147483647;pointer-events:none';
  const shadow = host.attachShadow({mode:'closed'});
  shadow.innerHTML = `<style>
    * { box-sizing:border-box }
    .edge { position:fixed;inset:3px;border:2px solid #a997ef;border-radius:10px;box-shadow:inset 0 0 22px #9575e94d;animation:breathe 1.5s ease-in-out infinite }
    .badge { position:fixed;top:12px;left:50%;transform:translateX(-50%);padding:9px 16px;border-radius:24px;background:#262139;color:white;font:13px system-ui;box-shadow:0 4px 18px #0003 }
    .cursor { position:fixed;left:0;top:0;transition:transform .09s ease;filter:drop-shadow(0 2px 3px #0005);display:none }
    .target { position:fixed;border:2px solid #9670ed;background:#9670ed18;border-radius:6px;display:none;transition:all .09s ease }
    .ring { position:absolute;left:-18px;top:-18px;width:40px;height:40px;border:3px solid #a083ef;border-radius:50%;animation:ripple .7s ease-out infinite }
    @keyframes breathe { 50% { opacity:.45 } }
    @keyframes ripple { from { transform:scale(.2);opacity:1 } to { transform:scale(1.5);opacity:0 } }
    @media(prefers-reduced-motion:reduce) { * { animation:none!important;transition:none!important } }
  </style><div class="edge"></div><div class="badge"></div><div class="target"></div>
  <div class="cursor"><div class="ring"></div><svg width="25" height="30" viewBox="0 0 25 30"><path d="M2 2L21 18L12 19L8 27Z" fill="#8b65dc" stroke="white" stroke-width="2"/></svg></div>`;
  let timer;
  window.__pluckActivity = (label, target, done = false) => {
    clearTimeout(timer);
    if (!host.isConnected) document.documentElement.append(host);
    host.style.display = '';
    shadow.querySelector('.badge').textContent = label;
    const cursor = shadow.querySelector('.cursor'), box = shadow.querySelector('.target');
    if (target) {
      const el = target === ':focus' ? document.activeElement : target.startsWith('@')
        ? document.querySelector('[data-pluck-native-ref="' + CSS.escape(target.slice(1)) + '"]') : document.querySelector(target);
      if (el) {
        el.scrollIntoView({block:'center',behavior:'instant'});
        const r = el.getBoundingClientRect();
        box.style.cssText += `;display:block;left:${r.left}px;top:${r.top}px;width:${r.width}px;height:${r.height}px`;
        cursor.style.display = 'block';
        requestAnimationFrame(() => cursor.style.transform = `translate(${r.left+r.width/2}px,${r.top+r.height/2}px)`);
      }
    } else if (!done) { cursor.style.display = 'none'; box.style.display = 'none'; }
    timer = setTimeout(() => { host.style.display = 'none'; }, done ? 700 : 35000);
    return true;
  };
})();
