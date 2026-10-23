// pluck as an MCP server over stdio, so Claude Code, Codex or any MCP client can drive the browser with tool calls.
// Register it yourself, e.g. `claude mcp add pluck -- node C:\path\to\mcp.mjs`; pluck never edits agent configs.
// Each server process is one browser session: its tabs are invisible to other sessions and closed when it exits.
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import readline from 'node:readline';
import { randomUUID } from 'node:crypto';

const owner = randomUUID();
const connectionPath = process.env.PLUCK_NATIVE_CONNECTION
  || path.join(process.env.APPDATA || path.join(os.homedir(), 'AppData', 'Roaming'), 'pluck-windows-native', 'connection.json');
let sessionId = process.env.PLUCK_SESSION || '';
const sessionLabel = process.env.PLUCK_SESSION_LABEL || 'MCP';

async function call(command, args = {}) {
  // Read the connection every call: pluck writes a new port and token each time it starts.
  let connection;
  try { connection = JSON.parse(await fs.readFile(connectionPath, 'utf8')); }
  catch { throw new Error('pluck is not running. Start pluck.exe first.'); }
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

let opened;
function session() {
  return opened ??= call('session', { session: sessionId, label: sessionLabel })
    .then(result => { sessionId = result.session; return sessionId; })
    .catch(error => { opened = undefined; throw error; });
}
const send = async (command, args = {}) => call(command, { session: await session(), owner, ...args });

const id = { type: 'integer', description: 'Tab id from open, tabs or active_tab' };
const target = { type: 'string', description: 'A snapshot ref such as "@f0:12", "text=Visible label", or a CSS selector' };
const tools = [
  { name: 'tabs', description: "List this session's tabs.", props: {}, run: () => send('list') },
  { name: 'open', description: 'Open a new background tab in this session. Does not change what the person is looking at.',
    props: { url: { type: 'string' } }, required: ['url'], run: a => send('new', { url: a.url }) },
  { name: 'active_tab', description: 'Get the tab the person is currently viewing and allow this session to drive it. It cannot be closed.',
    props: {}, run: () => send('active') },
  { name: 'goto', description: 'Navigate a tab and wait for the load to finish.', props: { id, url: { type: 'string' } }, required: ['id', 'url'],
    run: a => send('goto', a) },
  { name: 'snapshot', description: 'Read a tab: title, url, text, heading outline and interactive elements with role, name, value and state. Refs are renumbered every snapshot. diff=true returns only elements that changed since the previous snapshot of this tab, and text=null when the text is unchanged.',
    props: { id, full: { type: 'boolean', description: 'Up to 20,000 characters and 250 elements' }, diff: { type: 'boolean' } }, required: ['id'],
    run: a => send('snapshot', { id: a.id, textLimit: a.full ? 20000 : undefined, elementLimit: a.full ? 250 : undefined, diff: !!a.diff }) },
  { name: 'click', description: 'Click an element with a real (trusted) mouse event. Result input is "trusted" or "script" when it had to fall back.',
    props: { id, target }, required: ['id', 'target'], run: a => send('click', a) },
  { name: 'click_at', description: 'Click at viewport coordinates in CSS pixels, e.g. read from an annotated screenshot. Use for canvas or controls without refs.',
    props: { id, x: { type: 'number' }, y: { type: 'number' }, double: { type: 'boolean' } }, required: ['id', 'x', 'y'], run: a => send('clickAt', a) },
  { name: 'hover', description: 'Move the mouse over an element to open menus or tooltips.', props: { id, target }, required: ['id', 'target'], run: a => send('hover', a) },
  { name: 'fill', description: 'Replace the content of an input, textarea or contenteditable editor with text, as typing would.',
    props: { id, target, value: { type: 'string' } }, required: ['id', 'target', 'value'], run: a => send('fill', a) },
  { name: 'type', description: 'Type text into whatever is focused, without clearing it first.',
    props: { id, text: { type: 'string' } }, required: ['id', 'text'], run: a => send('type', { id: a.id, value: a.text }) },
  { name: 'select', description: 'Choose an option of a native <select> by value or visible text.',
    props: { id, target, option: { type: 'string' } }, required: ['id', 'target', 'option'], run: a => send('select', { id: a.id, target: a.target, value: a.option }) },
  { name: 'press', description: 'Press a key or combination: Enter, Tab, Escape, Backspace, Delete, arrows, Home, End, PageUp, PageDown, Space, F1-F12, single characters, with Control/Shift/Alt/Meta, e.g. "Control+A".',
    props: { id, key: { type: 'string' } }, required: ['id', 'key'], run: a => send('press', a) },
  { name: 'scroll', description: 'Scroll with the mouse wheel at the centre of the viewport.',
    props: { id, direction: { type: 'string', enum: ['up', 'down'] }, pixels: { type: 'integer' } }, required: ['id', 'direction'], run: a => send('scroll', a) },
  { name: 'back', description: 'Go back in tab history.', props: { id }, required: ['id'], run: a => send('back', a) },
  { name: 'forward', description: 'Go forward in tab history.', props: { id }, required: ['id'], run: a => send('forward', a) },
  { name: 'wait', description: 'Wait until a CSS selector exists, or until the page text contains "text=words".',
    props: { id, target: { type: 'string' }, timeout: { type: 'integer', description: 'Milliseconds, up to 30000' } }, required: ['id', 'target'], run: a => send('wait', a) },
  { name: 'screenshot', description: 'Capture the visible part of a tab as PNG. annotate=true draws the latest snapshot refs as numbered boxes.',
    props: { id, annotate: { type: 'boolean' } }, required: ['id'], image: true, run: a => send('screenshot', { id: a.id, annotate: !!a.annotate }) },
  { name: 'js', description: 'Evaluate a JavaScript expression in the page and return its JSON result. Prefer the other tools; they show the person what you are doing.',
    props: { id, code: { type: 'string' } }, required: ['id', 'code'], run: a => send('js', a) },
  { name: 'show', description: "Bring one of this session's tabs to the front only if the person is following it.", props: { id }, required: ['id'], run: a => send('show', a) },
  { name: 'close', description: "Close one of this session's tabs.", props: { id }, required: ['id'], run: a => send('close', a) },
];

const write = (message) => process.stdout.write(JSON.stringify({ jsonrpc: '2.0', ...message }) + '\n');

async function handle(message) {
  const { id: requestId, method, params } = message;
  if (requestId === undefined) return; // Notifications such as notifications/initialized need no reply.
  try {
    let result;
    if (method === 'initialize') {
      result = {
        protocolVersion: params?.protocolVersion || '2025-06-18',
        capabilities: { tools: {} },
        serverInfo: { name: 'pluck', version: '0.2.0' },
        instructions: 'Drive the pluck browser. It shares the person\'s logged-in profile: never buy, pay, post or send messages without their confirmation. Typical flow: open → snapshot → click/fill by ref → snapshot {diff:true}.',
      };
    } else if (method === 'ping') result = {};
    else if (method === 'tools/list') {
      result = { tools: tools.map(t => ({
        name: t.name, description: t.description,
        inputSchema: { type: 'object', properties: t.props, required: t.required ?? [], additionalProperties: false },
      })) };
    } else if (method === 'tools/call') {
      const tool = tools.find(t => t.name === params?.name);
      if (!tool) throw Object.assign(new Error(`Unknown tool: ${params?.name}`), { code: -32602 });
      try {
        const output = await tool.run(params.arguments ?? {});
        result = tool.image
          ? { content: [{ type: 'image', data: output.png, mimeType: 'image/png' }] }
          : { content: [{ type: 'text', text: JSON.stringify(output) }] };
      } catch (error) {
        result = { content: [{ type: 'text', text: error.message }], isError: true };
      }
    } else throw Object.assign(new Error(`Method not found: ${method}`), { code: -32601 });
    write({ id: requestId, result });
  } catch (error) {
    write({ id: requestId, error: { code: error.code ?? -32603, message: error.message } });
  }
}

const heartbeat = setInterval(() => {
  if (opened) call('heartbeat', { owner, session: sessionId }).catch(() => {});
}, 30000);
heartbeat.unref();

let closing;
async function shutdown(code) {
  closing ??= (async () => {
    clearInterval(heartbeat);
    if (opened) {
      await call('cleanup-run', { owner }).catch(() => {});
      await call('release', { session: sessionId }).catch(() => {});
    }
    process.exit(code);
  })();
  return closing;
}

const pending = new Set();
const lines = readline.createInterface({ input: process.stdin });
lines.on('line', line => {
  if (!line.trim()) return;
  let message;
  try { message = JSON.parse(line); }
  catch { write({ id: null, error: { code: -32700, message: 'Parse error' } }); return; }
  const work = handle(message).finally(() => pending.delete(work));
  pending.add(work);
});
lines.on('close', async () => { await Promise.allSettled([...pending]); await shutdown(0); });
for (const signal of ['SIGINT', 'SIGTERM']) process.once(signal, () => shutdown(signal === 'SIGINT' ? 130 : 143));
