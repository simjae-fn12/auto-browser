import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { randomUUID } from 'node:crypto';

const owner = randomUUID();
const args = process.argv.slice(2);
const keepTabs = args.includes('--keep-tabs');
const scriptPath = args.find(arg => arg !== '--keep-tabs');

const connectionPath = process.env.EGO_NATIVE_CONNECTION
  || path.join(process.env.APPDATA || path.join(os.homedir(), 'AppData', 'Roaming'), 'ego-windows-native', 'connection.json');
let connection;
try { connection = JSON.parse(await fs.readFile(connectionPath, 'utf8')); }
catch { throw new Error('Ego Windows Native를 먼저 실행하세요.'); }

async function call(command, args = {}) {
  const response = await fetch(`http://127.0.0.1:${connection.port}/`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', authorization: `Bearer ${connection.token}` },
    body: JSON.stringify({ command, ...args }),
    signal: AbortSignal.timeout(45000),
  });
  const result = await response.json();
  if (!response.ok) throw new Error(`${command}: ${result.error || response.status}`);
  return result;
}

function scoped(space) {
  const send = (command, args = {}) => call(command, { space, owner, ...args });
  return Object.freeze({
    name: space,
    list: () => send('list'),
    open: (url) => send('new', { url }),
    show: (id) => send('show', { id }),
    goto: (id, url) => send('goto', { id, url }),
    snapshot: (id, options = {}) => send('snapshot', { id,
      textLimit: options.full ? 20000 : options.textLimit,
      elementLimit: options.full ? 250 : options.elementLimit,
    }),
    click: (id, target) => send('click', { id, target }),
    fill: (id, target, value) => send('fill', { id, target, value }),
    press: (id, key) => send('press', { id, key }),
    scroll: (id, direction, pixels = 600) => send('scroll', { id, direction, pixels }),
    wait: (id, target, timeout = 10000) => send('wait', { id, target, timeout }),
    js: async (id, code) => (await send('js', { id, code })).result,
    screenshot: async (id, file) => {
      const { png } = await send('screenshot', { id });
      await fs.writeFile(file, Buffer.from(png, 'base64'));
      return path.resolve(file);
    },
    close: (id) => send('close', { id }),
  });
}

const browser = Object.freeze({
  listSpaces: () => call('spaces'),
  useOrCreateSpace: async (name) => {
    await call('space', { space: name });
    return scoped(name);
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
  call('heartbeat', { owner }).catch(error => console.error(`Agent heartbeat failed: ${error.message}`));
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
}
