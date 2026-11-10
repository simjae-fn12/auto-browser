// Page reading and agent input for one tab, ported from the Windows build (BrowserForm.cs, BrowserInput.cs,
// BrowserAgentExtras.cs). Input goes through the DevTools Input domain so pages see trusted events (event.isTrusted),
// exactly like a person's mouse and keyboard; script-made events are only a fallback.
const { nativeImage } = require('electron');

const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const json = value => JSON.stringify(value);

// ---------- DevTools ----------

function attach(wc) {
  if (!wc.debugger.isAttached()) wc.debugger.attach('1.3');
}

// DevTools rejects explicit nulls, so they are dropped.
async function cdp(wc, method, params = {}) {
  attach(wc);
  const clean = Object.fromEntries(Object.entries(params).filter(([, value]) => value !== null && value !== undefined));
  return wc.debugger.sendCommand(method, clean);
}

// ---------- Frames ----------

// "f0" is the top document; every other frame is named after its frame tree node and lives until it is destroyed.
function frameKey(frame) {
  const id = frame.frameTreeNodeId ?? frame.routingId;
  return `f${id === 0 ? 'x0' : id}`;
}

function framesOf(tab) {
  const main = tab.wc.mainFrame;
  const frames = [['f0', main]];
  for (const frame of main.framesInSubtree) {
    if (frame === main || frame.detached) continue;
    frames.push([frameKey(frame), frame]);
  }
  return frames;
}

function frameById(tab, frameId) {
  if (frameId === 'f0') return tab.wc.mainFrame;
  const found = framesOf(tab).find(([key]) => key === frameId);
  if (!found) throw new Error('Frame changed. Take a new snapshot.');
  return found[1];
}

// Runs an expression and keeps the page's own error message, such as "Element not found".
async function runInFrame(tab, frameId, expression) {
  const frame = frameById(tab, frameId);
  const wrapped = `(async () => { try { return { ok: true, value: await (${expression}) }; }
    catch (e) { return { ok: false, error: String((e && e.message) || e) }; } })()`;
  let result;
  try { result = await frame.executeJavaScript(wrapped); }
  catch (error) { throw new Error('Script failed. Take a new snapshot.'); }
  if (!result?.ok) throw new Error((result?.error || 'Script failed. Take a new snapshot.').split('\n')[0].replace(/^Error: /, '').trim());
  return result.value;
}

// ---------- Snapshot ----------

// Interactive elements with their accessible role, name and state, visible ones first, plus a heading outline.
const SNAPSHOT_SCRIPT = `
(() => {
  document.querySelectorAll('[data-oinzi-ref]').forEach(el => el.removeAttribute('data-oinzi-ref'));
  const selector = 'a[href],button,input:not([type=hidden]),textarea,select,summary,[contenteditable=""],[contenteditable="true"],'
    + '[role=button],[role=link],[role=checkbox],[role=radio],[role=switch],[role=tab],[role=menuitem],[role=menuitemcheckbox],'
    + '[role=option],[role=combobox],[role=textbox],[role=searchbox],[role=slider],[role=treeitem],[onclick],[tabindex]:not([tabindex="-1"])';
  const shown = el => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el); return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; };
  const inView = el => { const r = el.getBoundingClientRect(); return r.bottom > 0 && r.top < innerHeight && r.right > 0 && r.left < innerWidth; };
  const clean = t => (t || '').replace(/\\s+/g, ' ').trim();
  const role = el => {
    const explicit = el.getAttribute('role'); if (explicit) return explicit;
    const tag = el.tagName.toLowerCase(), type = (el.getAttribute('type') || 'text').toLowerCase();
    if (tag === 'a') return 'link';
    if (tag === 'button' || tag === 'summary') return 'button';
    if (tag === 'select') return el.multiple ? 'listbox' : 'combobox';
    if (tag === 'textarea' || el.isContentEditable) return 'textbox';
    if (tag === 'input') return { checkbox: 'checkbox', radio: 'radio', range: 'slider', search: 'searchbox', submit: 'button', button: 'button', reset: 'button', image: 'button', file: 'button' }[type] || 'textbox';
    return 'generic';
  };
  const name = el => {
    const labelled = el.getAttribute('aria-labelledby');
    const byIds = labelled ? clean(labelled.split(/\\s+/).map(id => document.getElementById(id)?.innerText).join(' ')) : '';
    return clean(el.getAttribute('aria-label')) || byIds || clean([...(el.labels || [])].map(l => l.innerText).join(' '))
      || clean(el.innerText) || clean(el.querySelector?.('img[alt]')?.getAttribute('alt')) || clean(el.getAttribute('title'))
      || clean(el.getAttribute('placeholder')) || (el.type === 'submit' || el.type === 'button' ? clean(el.value) : '');
  };
  const state = el => {
    const s = [];
    if (el.checked || el.getAttribute('aria-checked') === 'true') s.push('checked');
    if (el.disabled || el.getAttribute('aria-disabled') === 'true') s.push('disabled');
    const expanded = el.getAttribute('aria-expanded'); if (expanded) s.push(expanded === 'true' ? 'expanded' : 'collapsed');
    if (el.getAttribute('aria-selected') === 'true') s.push('selected');
    if (el.required) s.push('required');
    if (document.activeElement === el) s.push('focused');
    return s.join(' ');
  };
  const value = el => {
    if (el.tagName === 'SELECT') return clean(el.selectedOptions?.[0]?.text);
    if (!('value' in el) || el.tagName === 'BUTTON' || ['checkbox', 'radio', 'submit', 'button', 'reset', 'image', 'file'].includes(el.type)) return '';
    return el.type === 'password' ? (el.value ? '••••' : '') : String(el.value).slice(0, 160);
  };
  const all = [...document.querySelectorAll(selector)].filter(shown)
    .filter(el => !el.parentElement?.closest(selector) || el.matches('a[href],button,input,textarea,select'));
  const ordered = [...all.filter(inView), ...all.filter(el => !inView(el))].slice(0, __ELEMENT_LIMIT__);
  const elements = ordered.map((el, i) => {
    const ref = String(i + 1); el.setAttribute('data-oinzi-ref', ref);
    const item = { ref, tag: el.tagName.toLowerCase(), role: role(el), text: name(el).slice(0, 160), href: el.getAttribute('href') };
    const v = value(el); if (v) item.value = v;
    const s = state(el); if (s) item.state = s;
    if (!inView(el)) item.offscreen = true;
    return item;
  });
  const headings = [...document.querySelectorAll('h1,h2,h3')].filter(shown).slice(0, 30).map(h => h.tagName[1] + ' ' + clean(h.innerText).slice(0, 120));
  return { title: document.title, url: location.href, text: (document.body?.innerText || '').slice(0, __TEXT_LIMIT__), elements, headings,
    scroll: { y: Math.round(scrollY), height: document.documentElement.scrollHeight, viewport: innerHeight } };
})()`;

// A cheap stable hash, standing in for .NET's string.GetHashCode within one run.
function hash(text) {
  let h = 0;
  for (let i = 0; i < text.length; i++) h = (Math.imul(h, 31) + text.charCodeAt(i)) | 0;
  return h;
}

async function snapshot(tab, textLimit, elementLimit, diff) {
  const script = SNAPSHOT_SCRIPT.replace('__ELEMENT_LIMIT__', String(elementLimit)).replace('__TEXT_LIMIT__', String(textLimit));
  const results = [];
  for (const [id, frame] of framesOf(tab)) {
    try {
      const data = await frame.executeJavaScript(script);
      if (data) results.push([id, data]);
    } catch (error) {
      if (id === 'f0') throw new Error('Could not read the page: ' + error.message);
    }
  }
  const main = results.find(([id]) => id === 'f0')?.[1];
  const url = main?.url ?? '';
  const combined = results.map(([id, data]) => `[${id}] ${data.text}`).join('\n');
  const text = combined.slice(0, textLimit);
  const elements = results.flatMap(([id, data]) => (data.elements || []).map(e => ({ ...e, ref: `@${id}:${e.ref}` }))).slice(0, elementLimit);
  // Refs are renumbered every snapshot, so changes are tracked by what an element is; focus is left out of the key.
  const key = e => [e.ref.split(':')[0], e.role, e.text, e.href, e.value, (e.state || '').replace('focused', '').trim()].join('|');
  const keys = new Set(elements.map(key));
  const textHash = hash(text);
  const previous = diff && tab.lastSnapshotUrl === url ? tab.lastSnapshotKeys : null;
  const textUnchanged = !!previous && tab.lastSnapshotText === textHash;
  const changed = previous ? elements.filter(e => !previous.has(key(e))) : elements;
  const removed = previous ? [...previous].filter(k => !keys.has(k)).length : 0;
  tab.lastSnapshotKeys = keys;
  tab.lastSnapshotText = textHash;
  tab.lastSnapshotUrl = url;
  const dialogs = tab.dialogs.splice(0);
  return {
    title: main?.title ?? '', url,
    text: textUnchanged ? null : text,
    elements: changed,
    diff: previous ? { textUnchanged, unchanged: elements.length - changed.length, removed } : null,
    headings: main?.headings, scroll: main?.scroll,
    frames: results.map(([id, data]) => ({ frame: id, url: data.url })),
    dialogs,
  };
}

// ---------- Targets ----------

// Finds a target in a frame: a snapshot ref (@3), "text=Visible words", or a CSS selector.
const LOCATE = `
const __oinziLocate = (target) => {
  if (target.startsWith('@')) return document.querySelector('[data-oinzi-ref="' + CSS.escape(target.slice(1)) + '"]');
  if (target.startsWith('text=')) {
    const wanted = target.slice(5).trim().toLowerCase();
    const candidates = [...document.querySelectorAll('a,button,input,textarea,select,summary,label,[role],[onclick],[tabindex],h1,h2,h3,h4,li,td,span,div')];
    const visible = el => { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
    const name = el => (el.innerText || el.value || el.getAttribute('aria-label') || el.getAttribute('placeholder') || '').trim().toLowerCase();
    return candidates.filter(el => visible(el) && name(el) === wanted).at(-1)
      || candidates.filter(el => visible(el) && name(el).includes(wanted)).at(-1) || null;
  }
  return document.querySelector(target);
};`;

const frameIdOf = target => /^@(f[0-9x]+):/.exec(target)?.[1] ?? 'f0';
const selectorOf = target => { const m = /^@(f[0-9x]+):([0-9]+)$/.exec(target); return m ? '@' + m[2] : target; };

async function mouseClick(wc, x, y, clickCount = 1) {
  await cdp(wc, 'Input.dispatchMouseEvent', { type: 'mouseMoved', x, y, button: 'none' });
  await cdp(wc, 'Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', buttons: 1, clickCount });
  await cdp(wc, 'Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', buttons: 0, clickCount });
}

// Scrolls the target into view and returns its centre in top-level viewport pixels. The frame offset is only
// knowable for same-origin frames; elsewhere offsetKnown is false and the caller falls back to a script click.
function prepareTarget(tab, frameId, selector, probeClick) {
  return runInFrame(tab, frameId, `(() => {
    ${LOCATE}
    const el = __oinziLocate(${json(selector)});
    if (!el) throw new Error('Element not found. Take a new snapshot.');
    el.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' });
    const r = el.getBoundingClientRect();
    const x = r.left + r.width / 2, y = r.top + r.height / 2;
    const hit = document.elementFromPoint(x, y);
    const covered = !hit || !(hit === el || el.contains(hit) || hit.contains(el));
    let ox = 0, oy = 0, offsetKnown = true;
    try {
      for (let w = window; w !== w.top; w = w.parent) {
        const f = w.frameElement; if (!f) { offsetKnown = false; break; }
        const fr = f.getBoundingClientRect(); ox += fr.left + f.clientLeft; oy += fr.top + f.clientTop;
      }
    } catch { offsetKnown = false; }
    if (${probeClick}) {
      window.__oinziTrusted = false;
      const listener = e => { if (e.isTrusted && (e.target === el || el.contains(e.target))) window.__oinziTrusted = true; };
      document.addEventListener('click', listener, { capture: true, once: true });
    }
    return { x: x + ox, y: y + oy, covered, offsetKnown, w: r.width, h: r.height };
  })()`);
}

async function click(tab, target) {
  const frameId = frameIdOf(target), selector = selectorOf(target);
  let box = await prepareTarget(tab, frameId, selector, true);
  if (box?.offsetKnown && !box.covered) {
    // Moving the pointer can close hover menus and shift the layout, so measure again once it has arrived.
    await cdp(tab.wc, 'Input.dispatchMouseEvent', { type: 'mouseMoved', x: box.x, y: box.y, button: 'none' });
    box = (await prepareTarget(tab, frameId, selector, true)) ?? box;
    await mouseClick(tab.wc, box.x, box.y);
    // A navigation replaces the document, so a missing probe means the click already took effect.
    let probe;
    try { probe = await runInFrame(tab, frameId, 'window.__oinziTrusted'); }
    catch { return 'trusted'; }
    if (probe !== false) return 'trusted';
  }
  const done = await runInFrame(tab, frameId, `(() => { ${LOCATE} const el = __oinziLocate(${json(selector)}); if (!el) return false; el.focus?.(); el.click(); return true; })()`);
  if (done !== true) throw new Error('Element not found. Take a new snapshot.');
  return 'script';
}

async function fill(tab, target, value) {
  const frameId = frameIdOf(target), selector = selectorOf(target);
  const kind = await runInFrame(tab, frameId, `(() => {
    ${LOCATE}
    const el = __oinziLocate(${json(selector)});
    if (!el) throw new Error('Element not found. Take a new snapshot.');
    el.scrollIntoView({ block: 'center', behavior: 'instant' });
    el.focus();
    if ('value' in el && typeof el.select === 'function') { el.select(); return 'value'; }
    if (el.isContentEditable) {
      const range = document.createRange(); range.selectNodeContents(el);
      const selection = getSelection(); selection.removeAllRanges(); selection.addRange(range);
      return 'editable';
    }
    throw new Error('Element cannot be filled');
  })()`);
  // insertText replaces the selection exactly as typing would, firing beforeinput/input for editors like React and ProseMirror.
  if (value.length > 0) await cdp(tab.wc, 'Input.insertText', { text: value });
  else await press(tab.wc, 'Delete');
  const compare = kind === 'value' ? 'el.value === wanted'
    : `(el.innerText || '').replace(/\\s+/g, ' ').trim() === wanted.replace(/\\s+/g, ' ').trim()`;
  const ok = await runInFrame(tab, frameId, `(() => { ${LOCATE} const el = __oinziLocate(${json(selector)}); if (!el) return false; const wanted = ${json(value)}; return ${compare}; })()`);
  if (ok === true) return 'trusted';
  if (kind !== 'value') return 'trusted-unverified';
  await runInFrame(tab, frameId, `(() => {
    ${LOCATE}
    const el = __oinziLocate(${json(selector)}); if (!el) return false;
    const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
    if (setter) setter.call(el, ${json(value)}); else el.value = ${json(value)};
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
  })()`);
  return 'script';
}

async function hover(tab, target) {
  const box = await prepareTarget(tab, frameIdOf(target), selectorOf(target), false);
  if (!box?.offsetKnown) throw new Error('Cannot hover inside a cross-origin frame');
  await cdp(tab.wc, 'Input.dispatchMouseEvent', { type: 'mouseMoved', x: box.x, y: box.y, button: 'none' });
}

function selectOption(tab, target, value) {
  return runInFrame(tab, frameIdOf(target), `(() => {
    ${LOCATE}
    const el = __oinziLocate(${json(selectorOf(target))});
    if (!el || el.tagName !== 'SELECT') throw new Error('Target is not a <select>. Click custom dropdowns instead.');
    const wanted = ${json(value)};
    const option = [...el.options].find(o => o.value === wanted) || [...el.options].find(o => o.text.trim() === wanted)
      || [...el.options].find(o => o.text.trim().toLowerCase().includes(wanted.toLowerCase()));
    if (!option) throw new Error('No option matches: ' + wanted + '. Options: ' + [...el.options].map(o => o.text.trim()).join(' | '));
    el.focus(); el.value = option.value;
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
    return option.text.trim();
  })()`);
}

// ---------- Keyboard and wheel ----------

const NAMED_KEYS = {
  enter: [13, 'Enter', '\r'], tab: [9, 'Tab', null], escape: [27, 'Escape', null], backspace: [8, 'Backspace', null],
  delete: [46, 'Delete', null], space: [32, 'Space', ' '], arrowup: [38, 'ArrowUp', null], arrowdown: [40, 'ArrowDown', null],
  arrowleft: [37, 'ArrowLeft', null], arrowright: [39, 'ArrowRight', null], home: [36, 'Home', null], end: [35, 'End', null],
  pageup: [33, 'PageUp', null], pagedown: [34, 'PageDown', null], insert: [45, 'Insert', null],
};

// Accepts "Enter", "a", "Control+A", "Shift+Tab", "Control+Shift+ArrowLeft", F1–F12.
async function press(wc, combo) {
  const parts = String(combo).split('+').map(part => part.trim()).filter(Boolean);
  if (parts.length === 0) throw new Error('Key required');
  let modifiers = 0;
  for (const modifier of parts.slice(0, -1)) {
    const bit = { alt: 1, control: 2, ctrl: 2, meta: 4, win: 4, shift: 8 }[modifier.toLowerCase()];
    if (!bit) throw new Error(`Unknown modifier: ${modifier}`);
    modifiers |= bit;
  }
  const last = parts.at(-1);
  let key = last === 'Space' ? ' ' : last, vk, code, text;
  const named = NAMED_KEYS[last.toLowerCase()];
  if (named) { [vk, code, text] = named; key = last === 'Space' ? ' ' : code; }
  else if (key.length === 1) {
    const c = key, upper = c.toUpperCase();
    const letter = /\p{L}/u.test(c);
    vk = /^[A-Z0-9]$/.test(upper) ? upper.charCodeAt(0) : 0;
    code = letter && c.charCodeAt(0) < 128 ? 'Key' + upper : /^[0-9]$/.test(c) ? 'Digit' + c : '';
    text = (modifiers & ~8) === 0 ? ((modifiers & 8) ? upper : c) : null;
    // Shortcuts name the produced character, so Control+A reports key 'a' like a real keyboard.
    key = (modifiers & 8) && letter ? upper : letter ? c.toLowerCase() : c;
  } else if (/^F([1-9]|1[0-2])$/.test(key)) { vk = 111 + Number(key.slice(1)); code = key; text = null; }
  else throw new Error(`Unsupported key: ${key}`);
  // Ctrl/Alt shortcuts carry no text, so Chromium treats them as commands such as select-all.
  if (modifiers & 3) text = null;
  await cdp(wc, 'Input.dispatchKeyEvent', {
    type: text == null ? 'rawKeyDown' : 'keyDown', modifiers, key, code, windowsVirtualKeyCode: vk, nativeVirtualKeyCode: vk,
    text, unmodifiedText: text,
  });
  await cdp(wc, 'Input.dispatchKeyEvent', { type: 'keyUp', modifiers, key, code, windowsVirtualKeyCode: vk, nativeVirtualKeyCode: vk });
}

async function wheel(wc, direction, pixelsText) {
  if (direction !== 'up' && direction !== 'down') throw new Error('Direction must be up or down');
  const parsed = parseInt(pixelsText, 10);
  const pixels = Number.isFinite(parsed) ? Math.min(10000, Math.max(1, parsed)) : 600;
  const view = await wc.executeJavaScript('({ w: innerWidth, h: innerHeight, y: scrollY })');
  const before = view?.y ?? 0;
  // A wheel scrolls whatever is under the pointer, including inner scroll panes that window.scrollBy misses.
  await cdp(wc, 'Input.dispatchMouseEvent', {
    type: 'mouseWheel', x: (view?.w ?? 800) / 2, y: (view?.h ?? 600) / 2, deltaX: 0, deltaY: direction === 'down' ? pixels : -pixels,
  });
  await delay(120);
  if ((await wc.executeJavaScript('scrollY')) !== before) return true;
  // Some pages swallow wheel events; still move the document so the agent is not stuck.
  await wc.executeJavaScript(`window.scrollBy(0, ${direction === 'down' ? pixels : -pixels})`);
  return true;
}

// ---------- Screenshots ----------

// Draws each snapshot ref as a numbered badge, captures, then removes the badges.
const ANNOTATE_SCRIPT = `(() => {
  document.getElementById('__oinzi_marks')?.remove();
  const layer = document.createElement('div'); layer.id = '__oinzi_marks';
  layer.style.cssText = 'position:fixed;inset:0;pointer-events:none;z-index:2147483646';
  for (const el of document.querySelectorAll('[data-oinzi-ref]')) {
    const r = el.getBoundingClientRect();
    if (r.width <= 0 || r.height <= 0 || r.bottom < 0 || r.top > innerHeight || r.right < 0 || r.left > innerWidth) continue;
    const box = document.createElement('div');
    box.style.cssText = \`position:fixed;left:\${r.left}px;top:\${r.top}px;width:\${r.width}px;height:\${r.height}px;outline:2px solid #e5484d;border-radius:3px\`;
    const tag = document.createElement('span'); tag.textContent = el.getAttribute('data-oinzi-ref');
    tag.style.cssText = 'position:absolute;left:-2px;top:-16px;background:#e5484d;color:#fff;font:600 11px/14px system-ui;padding:0 4px;border-radius:3px';
    box.append(tag); layer.append(box);
  }
  document.documentElement.append(layer);
  return layer.childElementCount;
})()`;

const timeout = (ms, message) => new Promise((_, reject) => setTimeout(() => reject(new Error(message)), ms));

// DevTools capture works for tabs that are not on screen, but a hidden tab may not paint a new frame for a long time.
// If the plain capture stalls, capturing beyond the viewport forces a fresh layout and paint.
async function capture(wc, { annotate = false, format = 'png', quality } = {}) {
  if (annotate) await wc.executeJavaScript(ANNOTATE_SCRIPT);
  try {
    const plain = cdp(wc, 'Page.captureScreenshot', { format, quality });
    plain.catch(() => {});
    let result = await Promise.race([plain, delay(2500).then(() => null)]);
    if (!result) {
      const view = await wc.executeJavaScript('({ w: innerWidth, h: innerHeight, x: scrollX, y: scrollY })');
      const forced = cdp(wc, 'Page.captureScreenshot', {
        format, quality, captureBeyondViewport: true,
        clip: { x: view?.x ?? 0, y: view?.y ?? 0, width: view?.w ?? 800, height: view?.h ?? 600, scale: 1 },
      });
      forced.catch(() => {});
      result = await Promise.race([plain, forced, timeout(15000, 'Screenshot timed out: the tab is not painting. Call show(id) and retry.')]);
    }
    if (!result?.data) throw new Error('Screenshot failed');
    return result.data;
  } finally {
    if (annotate) await wc.executeJavaScript("document.getElementById('__oinzi_marks')?.remove()").catch(() => {});
  }
}

// Compares the tab's current screen with a baseline PNG the agent sends as base64.
async function compare(wc, baseline, threshold, wantDiff) {
  if (!baseline) throw new Error('baseline (base64 PNG) required');
  const load = data => {
    const image = nativeImage.createFromBuffer(Buffer.from(data, 'base64'));
    if (image.isEmpty()) throw new Error('Not a PNG image');
    return image;
  };
  const before = load(baseline);
  const after = load(await capture(wc));
  const a = before.getSize(), b = after.getSize();
  const width = Math.min(a.width, b.width), height = Math.min(a.height, b.height);
  // toBitmap() is BGRA on every platform Electron supports.
  const pa = before.toBitmap(), pb = after.toBitmap();
  const limit = Math.round(Math.min(1, Math.max(0, Number.isFinite(threshold) ? threshold : 0.1)) * 255);
  const out = wantDiff ? Buffer.alloc(width * height * 4) : null;
  let changed = 0;
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const i = (y * a.width + x) * 4, j = (y * b.width + x) * 4, o = (y * width + x) * 4;
      const d = Math.max(Math.abs(pa[i] - pb[j]), Math.abs(pa[i + 1] - pb[j + 1]), Math.abs(pa[i + 2] - pb[j + 2]));
      if (d > limit) {
        changed++;
        if (out) { out[o] = 0x4d; out[o + 1] = 0x48; out[o + 2] = 0xe5; out[o + 3] = 255; }
      } else if (out) {
        // Unchanged pixels are kept faint so the red changes stand out against the page.
        const grey = Math.floor((pb[j] + pb[j + 1] + pb[j + 2]) / 3);
        const faint = 255 - Math.floor((255 - grey) / 4);
        out[o] = out[o + 1] = out[o + 2] = faint; out[o + 3] = 255;
      }
    }
  }
  const total = Math.max(1, width * height);
  return {
    changed: Math.round((changed / total) * 1e5) / 1e5, pixels: changed, width, height,
    sizeMatch: a.width === b.width && a.height === b.height,
    diff: out ? nativeImage.createFromBitmap(out, { width, height }).toPNG().toString('base64') : null,
  };
}

async function waitFor(tab, selector, ms) {
  if (!selector) throw new Error('CSS selector or text=... required');
  const deadline = Date.now() + Math.min(30000, Math.max(100, ms));
  const probe = selector.startsWith('text=')
    ? `(document.body?.innerText || '').toLowerCase().includes(${json(selector.slice(5).trim().toLowerCase())})`
    : `Boolean(document.querySelector(${json(selector)}))`;
  do {
    try { if (await tab.wc.executeJavaScript(probe) === true) return true; }
    catch { /* navigation in progress */ }
    await delay(200);
  } while (Date.now() < deadline);
  throw new Error(`Timed out waiting for ${selector}`);
}

module.exports = {
  cdp, attach, framesOf, frameIdOf, selectorOf, runInFrame, snapshot, click, fill, hover, selectOption,
  press, wheel, mouseClick, capture, compare, waitFor, delay,
};
