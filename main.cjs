const { app, BrowserWindow, WebContentsView, ipcMain, session } = require('electron');
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

app.setName('ego-windows');
const tabs = new Map();
let window;
let activeId;
let nextId = 1;
let server;
let saveTimer;

function saveTabs() {
  const urls = [...tabs.values()].filter(tab => !tab.agent)
    .map(tab => tab.view.webContents.getURL())
    .filter(url => /^https?:\/\//.test(url));
  fs.writeFileSync(path.join(app.getPath('userData'), 'tabs.json'), JSON.stringify(urls));
}

function scheduleSave() {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(saveTabs, 500);
}

function normalizeUrl(input) {
  const value = String(input || '').trim();
  if (!value) return 'https://www.google.com';
  if (/^https?:\/\//i.test(value)) return value;
  if (/^(localhost|127\.0\.0\.1)(:\d+)?(\/|$)/i.test(value)) return `http://${value}`;
  if (/^[^\s]+\.[^\s]+$/.test(value)) return `https://${value}`;
  return `https://www.google.com/search?q=${encodeURIComponent(value)}`;
}

function state() {
  return {
    active: activeId,
    tabs: [...tabs.values()].map(({ id, view, agent }) => ({
      id, agent, title: view.webContents.getTitle(), url: view.webContents.getURL(),
    })),
  };
}

function broadcast() {
  if (window && !window.isDestroyed()) window.webContents.send('browser-state', state());
  scheduleSave();
}

function resize() {
  if (!window || !activeId) return;
  const [width, height] = window.getContentSize();
  tabs.get(activeId)?.view.setBounds({ x: 0, y: 86, width, height: Math.max(0, height - 86) });
}

function selectTab(id) {
  const tab = tabs.get(Number(id));
  if (!tab) throw new Error(`Unknown tab: ${id}`);
  if (activeId && tabs.has(activeId)) window.contentView.removeChildView(tabs.get(activeId).view);
  activeId = tab.id;
  window.contentView.addChildView(tab.view);
  resize();
  broadcast();
  return tab.id;
}

function createTab(url, agent = false, visible = !agent) {
  const id = nextId++;
  const view = new WebContentsView({ webPreferences: { session: session.fromPartition('persist:shared'), sandbox: true } });
  tabs.set(id, { id, view, agent, refFrames: new Map() });
  const contents = view.webContents;
  for (const event of ['did-navigate', 'did-navigate-in-page', 'page-title-updated', 'did-finish-load']) contents.on(event, broadcast);
  contents.setWindowOpenHandler(({ url: popupUrl }) => {
    createTab(popupUrl, agent, true);
    return { action: 'deny' };
  });
  contents.on('before-input-event', (event, input) => {
    if (input.type !== 'keyDown') return;
    const mod = input.control || input.meta;
    const key = input.key.toLowerCase();
    if (mod && key === 'l') { event.preventDefault(); window.webContents.send('focus-address'); window.webContents.focus(); }
    if (mod && key === 't') { event.preventDefault(); createTab('https://www.google.com'); }
    if (mod && key === 'w') { event.preventDefault(); closeTab(id); }
    if (input.alt && key === 'left' && contents.canGoBack()) { event.preventDefault(); contents.goBack(); }
    if (input.alt && key === 'right' && contents.canGoForward()) { event.preventDefault(); contents.goForward(); }
  });
  if (visible || !activeId) selectTab(id);
  contents.loadURL(normalizeUrl(url)).catch(() => {});
  broadcast();
  return id;
}

function getTab(id) {
  const tab = tabs.get(Number(id));
  if (!tab) throw new Error(`Unknown tab: ${id}`);
  return tab;
}

function closeTab(id) {
  const tab = getTab(id);
  if (activeId === tab.id) {
    window.contentView.removeChildView(tab.view);
    activeId = undefined;
  }
  tabs.delete(tab.id);
  tab.view.webContents.close();
  if (!activeId) {
    const remaining = [...tabs.keys()][0];
    if (remaining) selectTab(remaining); else createTab('https://www.google.com');
  }
  broadcast();
  return { ok: true };
}

async function snapshot(tab) {
  const frames = tab.view.webContents.mainFrame.framesInSubtree;
  tab.refFrames = new Map();
  const results = [];
  for (const [index, frame] of frames.entries()) {
    const frameId = `f${index}`;
    tab.refFrames.set(frameId, frame);
    try {
      const result = await frame.executeJavaScript(`(() => {
    const nodes = [...document.querySelectorAll('a,button,input,textarea,select,[role="button"],[contenteditable="true"]')]
      .filter(el => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el); return r.width && r.height && s.visibility !== 'hidden' && s.display !== 'none'; })
      .slice(0, 250);
    document.querySelectorAll('[data-ego-windows-ref]').forEach(el => el.removeAttribute('data-ego-windows-ref'));
    const elements = nodes.map((el, i) => {
      const ref = String(i + 1); el.setAttribute('data-ego-windows-ref', ref);
      return { ref, tag: el.tagName.toLowerCase(), role: el.getAttribute('role'),
        text: (el.innerText || el.value || el.getAttribute('aria-label') || el.getAttribute('placeholder') || '').trim().slice(0, 160),
        href: el.getAttribute('href') };
    });
    return { title: document.title, url: location.href, text: (document.body?.innerText || '').slice(0, 12000), elements };
  })()`);
      results.push({ frame: frameId, ...result, elements: result.elements.map(el => ({ ...el, ref: `@${frameId}:${el.ref}` })) });
    } catch { /* Frame navigated while taking the snapshot */ }
  }
  const top = results[0] || { title: '', url: '', text: '', elements: [] };
  return { title: top.title, url: top.url, text: results.map(result => `[${result.frame}] ${result.text}`).join('\n').slice(0, 20000),
    elements: results.flatMap(result => result.elements.map(el => ({ ...el, frame: result.url }))),
    frames: results.map(({ frame, url }) => ({ frame, url })) };
}

async function act(tab, command, target, value) {
  if (typeof target !== 'string' || !target) throw new Error('Target must be a ref or CSS selector');
  const match = /^@(?<frame>f\d+):(?<ref>\d+)$/.exec(target);
  const frame = match ? tab.refFrames.get(match.groups.frame) : tab.view.webContents.mainFrame;
  if (!frame || frame.isDestroyed()) throw new Error('Frame changed. Take a new snapshot.');
  const selector = match ? `@${match.groups.ref}` : target;
  const script = `(() => {
    const target = ${JSON.stringify(selector)};
    const value = ${JSON.stringify(value)};
    const el = target.startsWith('@')
      ? document.querySelector('[data-ego-windows-ref="' + CSS.escape(target.slice(1)) + '"]')
      : document.querySelector(target);
    if (!el) throw new Error('Element not found. Take a new snapshot.');
    el.scrollIntoView({ block: 'center' });
    if (${JSON.stringify(command)} === 'click') { el.click(); return true; }
    if (${JSON.stringify(command)} === 'fill') {
      if (!('value' in el)) throw new Error('Element cannot be filled');
      const setter = Object.getOwnPropertyDescriptor(el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype, 'value')?.set;
      if (setter) setter.call(el, value); else el.value = value;
      el.dispatchEvent(new Event('input', { bubbles: true }));
      el.dispatchEvent(new Event('change', { bubbles: true }));
      return true;
    }
  })()`;
  return frame.executeJavaScript(script, true);
}

async function command(body) {
  if (body.command === 'list') return state();
  if (body.command === 'new') return { id: createTab(body.url, true, Boolean(body.visible)) };
  if (body.command === 'show') return { id: selectTab(body.id) };
  const tab = getTab(body.id);
  const contents = tab.view.webContents;
  if (body.command === 'goto') { await contents.loadURL(normalizeUrl(body.url)); return { url: contents.getURL() }; }
  if (body.command === 'snapshot') return snapshot(tab);
  if (body.command === 'click' || body.command === 'fill') return { ok: await act(tab, body.command, body.target, body.value) };
  if (body.command === 'screenshot') {
    const image = await contents.capturePage(undefined, { stayHidden: true });
    return { png: image.toPNG().toString('base64') };
  }
  if (body.command === 'js') {
    const frame = body.frame ? tab.refFrames.get(body.frame) : contents.mainFrame;
    if (!frame || frame.isDestroyed()) throw new Error('Frame changed. Take a new snapshot.');
    return { result: await frame.executeJavaScript(String(body.code || ''), true) };
  }
  if (body.command === 'wait') {
    const selector = String(body.target || '');
    if (!selector) throw new Error('CSS selector required');
    const deadline = Date.now() + Math.min(Math.max(Number(body.timeout) || 10000, 100), 30000);
    do {
      if (await contents.executeJavaScript(`Boolean(document.querySelector(${JSON.stringify(selector)}))`).catch(() => false)) return { ok: true };
      await new Promise(resolve => setTimeout(resolve, 200));
    } while (Date.now() < deadline);
    throw new Error(`Timed out waiting for ${selector}`);
  }
  if (body.command === 'close') {
    return closeTab(tab.id);
  }
  throw new Error(`Unknown command: ${body.command}`);
}

function startServer() {
  const token = crypto.randomBytes(32).toString('hex');
  server = http.createServer(async (request, response) => {
    response.setHeader('Content-Type', 'application/json; charset=utf-8');
    if (request.method !== 'POST' || request.headers.authorization !== `Bearer ${token}`) {
      response.writeHead(401).end(JSON.stringify({ error: 'Unauthorized' }));
      return;
    }
    try {
      let raw = '';
      for await (const chunk of request) {
        raw += chunk;
        if (raw.length > 1024 * 1024) throw new Error('Request too large');
      }
      const result = await command(JSON.parse(raw));
      response.writeHead(200).end(JSON.stringify(result));
    } catch (error) {
      response.writeHead(400).end(JSON.stringify({ error: error.message }));
    }
  });
  server.listen(0, '127.0.0.1', () => {
    const connection = path.join(app.getPath('userData'), 'connection.json');
    fs.writeFileSync(connection, JSON.stringify({ port: server.address().port, token }), { mode: 0o600 });
    app.on('before-quit', () => { server.close(); fs.rmSync(connection, { force: true }); });
  });
}

app.whenReady().then(() => {
  window = new BrowserWindow({ width: 1200, height: 800, minWidth: 600, minHeight: 400,
    webPreferences: { preload: path.join(__dirname, 'preload.cjs'), contextIsolation: true, nodeIntegration: false } });
  window.loadFile(path.join(__dirname, 'chrome.html'));
  window.on('resize', resize);
  window.webContents.on('did-finish-load', broadcast);
  ipcMain.handle('ui-action', async (_event, name, value) => {
    if (name === 'new') return createTab('https://www.google.com');
    if (name === 'select') return selectTab(value);
    if (name === 'close') return closeTab(value);
    const contents = getTab(activeId).view.webContents;
    if (name === 'goto') return contents.loadURL(normalizeUrl(value));
    if (name === 'back' && contents.canGoBack()) return contents.goBack();
    if (name === 'forward' && contents.canGoForward()) return contents.goForward();
    if (name === 'reload') return contents.reload();
  });
  window.webContents.on('before-input-event', (event, input) => {
    if (input.type !== 'keyDown') return;
    const mod = input.control || input.meta;
    const key = input.key.toLowerCase();
    if (mod && key === 'l') { event.preventDefault(); window.webContents.send('focus-address'); }
    if (mod && key === 't') { event.preventDefault(); createTab('https://www.google.com'); }
    if (mod && key === 'w') { event.preventDefault(); closeTab(activeId); }
  });
  let restored = [];
  try { restored = JSON.parse(fs.readFileSync(path.join(app.getPath('userData'), 'tabs.json'), 'utf8')); }
  catch { /* First launch */ }
  const urls = Array.isArray(restored) ? restored.filter(url => /^https?:\/\//.test(url)).slice(0, 20) : [];
  for (const url of urls.length ? urls : ['https://www.google.com']) createTab(url);
  startServer();
});

app.on('before-quit', () => { clearTimeout(saveTimer); saveTabs(); });
app.on('window-all-closed', () => app.quit());
