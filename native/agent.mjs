import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { randomUUID } from 'node:crypto';

const owner = randomUUID();
const args = process.argv.slice(2);
let keepTabs = false;
let scriptPath;
// The AI panel passes its conversation's session in the environment; a terminal agent passes --session to reuse its tabs.
let sessionId = process.env.PLUCK_SESSION || '';
let sessionLabel = process.env.PLUCK_SESSION_LABEL || '';
// --profile / PLUCK_PROFILE pins the session to a browser profile, so its tabs use that profile's logins.
let sessionProfile = process.env.PLUCK_PROFILE || '';
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--keep-tabs') keepTabs = true;
  else if (args[i] === '--session') sessionId = args[++i] ?? '';
  else if (args[i] === '--label') sessionLabel = args[++i] ?? '';
  else if (args[i] === '--profile') sessionProfile = args[++i] ?? '';
  else scriptPath ??= args[i];
}

const connectionPath = process.env.PLUCK_NATIVE_CONNECTION
  || path.join(process.env.APPDATA || path.join(os.homedir(), 'AppData', 'Roaming'), 'pluck-windows-native', 'connection.json');
let connection;
try { connection = JSON.parse(await fs.readFile(connectionPath, 'utf8')); }
catch { throw new Error('pluck Windows Native를 먼저 실행하세요.'); }

async function call(command, args = {}) {
  const response = await fetch(`http://127.0.0.1:${connection.port}/`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', authorization: `Bearer ${connection.token}` },
    body: JSON.stringify({ command, ...args }),
    signal: AbortSignal.timeout(command === 'batch' ? 300000 : 45000),
  });
  const result = await response.json();
  if (!response.ok) throw new Error(`${command}: ${result.error || response.status}`);
  return result;
}

// { full } raises the limits; { diff } returns only elements that changed since this tab's previous snapshot.
const snapshotArgs = (options) => ({
  textLimit: options.full ? 20000 : options.textLimit,
  elementLimit: options.full ? 250 : options.elementLimit,
  diff: !!options.diff,
});

function scoped(session, label, profile) {
  const send = (command, args = {}) => call(command, { session, owner, ...args });
  return Object.freeze({
    id: session,
    name: label,
    profile,
    list: () => send('list'),
    // Tabs open in the session's profile; { profile } picks another (name or id from browser.profiles()).
    open: (url, options = {}) => send('new', { url, profile: options.profile }),
    show: (id) => send('show', { id }),
    goto: (id, url) => send('goto', { id, url }),
    snapshot: (id, options = {}) => send('snapshot', { id, ...snapshotArgs(options) }),
    click: (id, target) => send('click', { id, target }),
    clickAt: (id, x, y, options = {}) => send('clickAt', { id, x, y, double: !!options.double }),
    hover: (id, target) => send('hover', { id, target }),
    fill: (id, target, value) => send('fill', { id, target, value }),
    type: (id, text) => send('type', { id, value: text }),
    select: (id, target, option) => send('select', { id, target, value: option }),
    press: (id, key) => send('press', { id, key }),
    scroll: (id, direction, pixels = 600) => send('scroll', { id, direction, pixels }),
    back: (id) => send('back', { id }),
    forward: (id) => send('forward', { id }),
    wait: (id, target, timeout = 10000) => send('wait', { id, target, timeout }),
    js: async (id, code) => (await send('js', { id, code })).result,
    screenshot: async (id, file, options = {}) => {
      const { png } = await send('screenshot', { id, annotate: !!options.annotate });
      await fs.writeFile(file, Buffer.from(png, 'base64'));
      return path.resolve(file);
    },
    close: (id) => send('close', { id }),
    login: (id, credential) => send('login', { id, credential }),
    // One line on this session's overview card, e.g. '검증 중' or '막힘: 캡차'.
    status: (text) => send('status', { text }),
    // Runs [{ command: 'click', id, target }, ...] in order; stops at the first failure unless { bail: false }.
    batch: (steps, options = {}) => send('batch', { steps, bail: options.bail !== false }),
    // Compares the tab with a baseline PNG file; { diff: 'out.png' } also saves an image with changes in red.
    compare: async (id, baselineFile, options = {}) => {
      const baseline = (await fs.readFile(baselineFile)).toString('base64');
      const result = await send('compare', { id, baseline, threshold: options.threshold, diff: !!options.diff });
      if (options.diff && result.diff) await fs.writeFile(options.diff, Buffer.from(result.diff, 'base64'));
      delete result.diff;
      return result;
    },
  });
}

let opened;
function session(label) {
  return opened ??= call('session', { session: sessionId, label: label || sessionLabel, profile: sessionProfile }).then(result => {
    if (result.created && keepTabs)
      console.error(`pluck session ${result.session}: pass --session ${result.session} to the next run to keep using these tabs.`);
    sessionId = result.session;
    return scoped(result.session, result.label, result.profile);
  });
}

const browser = Object.freeze({
  session,
  // The person's browser profiles and the site logins saved in each (never the passwords).
  profiles: async () => { await session(); return (await call('profiles', { session: sessionId })).profiles; },
  // Older scripts named a shared space; the name is now only this session's label.
  useOrCreateSpace: (name) => session(name),
  // The tab the person is looking at. It cannot be opened or closed from here, only read and driven.
  activeTab: async () => {
    await session();
    const tab = await call('active', { session: sessionId });
    const send = (command, args = {}) => call(command, { session: sessionId, owner, ...args, id: tab.id });
    return Object.freeze({
      id: tab.id, title: tab.title, url: tab.url,
      goto: (url) => send('goto', { url }),
      snapshot: (options = {}) => send('snapshot', snapshotArgs(options)),
      click: (target) => send('click', { target }),
      clickAt: (x, y, options = {}) => send('clickAt', { x, y, double: !!options.double }),
      hover: (target) => send('hover', { target }),
      fill: (target, value) => send('fill', { target, value }),
      type: (text) => send('type', { value: text }),
      select: (target, option) => send('select', { target, value: option }),
      press: (key) => send('press', { key }),
      scroll: (direction, pixels = 600) => send('scroll', { direction, pixels }),
      back: () => send('back'),
      forward: () => send('forward'),
      wait: (target, timeout = 10000) => send('wait', { target, timeout }),
      js: async (code) => (await send('js', { code })).result,
      login: (credential) => send('login', { credential }),
      screenshot: async (file, options = {}) => {
        const { png } = await send('screenshot', { annotate: !!options.annotate });
        await fs.writeFile(file, Buffer.from(png, 'base64'));
        return path.resolve(file);
      },
    });
  },
});

const source = scriptPath
  ? await fs.readFile(scriptPath, 'utf8')
  : await new Promise((resolve, reject) => {
      let input = '';
      process.stdin.setEncoding('utf8');
      process.stdin.on('data', chunk => { input += chunk; });
      process.stdin.on('end', () => resolve(input));
      process.stdin.on('error', reject);
    });
if (!source.trim()) throw new Error('JavaScript를 표준 입력으로 전달하거나 스크립트 파일 경로를 지정하세요.');
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
let cleanupPromise;
function cleanup() {
  return cleanupPromise ??= call('cleanup-run', { owner }).catch(error => {
    console.error(`Agent tab cleanup failed: ${error.message}`);
    process.exitCode = 1;
  });
}
const heartbeat = setInterval(() => {
  call('heartbeat', { owner, session: sessionId }).catch(error => console.error(`Agent heartbeat failed: ${error.message}`));
}, 30000);
heartbeat.unref();
for (const signal of ['SIGINT', 'SIGTERM']) {
  process.once(signal, async () => {
    clearInterval(heartbeat);
    await cleanup();
    process.exit(signal === 'SIGINT' ? 130 : 143);
  });
}
let succeeded = false;
try {
  await new AsyncFunction('browser', source)(browser);
  succeeded = true;
} finally {
  clearInterval(heartbeat);
  if (!keepTabs || !succeeded) await cleanup();
  if (opened) await call('release', { session: sessionId }).catch(() => {});
}
