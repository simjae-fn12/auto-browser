// Drives a real pluck build: pages must see trusted input events, and snapshots must carry roles, states and diffs.
// Build first: dotnet build native/native.csproj -c Debug. Runs against a private data folder, not your profile.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import readline from 'node:readline';
import { spawn } from 'node:child_process';

const root = path.resolve(import.meta.dirname, '..');
const exe = path.join(root, 'native', 'bin', 'Debug', 'net10.0-windows', 'pluck.exe');
const fixtures = path.join(root, 'tests', 'fixtures');

async function start(t) {
  const server = http.createServer(async (req, res) => {
    try {
      const body = await fs.readFile(path.join(fixtures, path.basename(req.url.split('?')[0]) || 'page.html'));
      res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' }).end(body);
    } catch { res.writeHead(404).end(); }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const data = await fs.mkdtemp(path.join(os.tmpdir(), 'pluck-input-'));
  const app = spawn(exe, [], { env: { ...process.env, PLUCK_NATIVE_DATA_DIR: data }, stdio: 'ignore' });
  const connection = path.join(data, 'connection.json');
  for (let i = 0; i < 60; i++) {
    try { await fs.access(connection); break; } catch { await new Promise(r => setTimeout(r, 500)); }
  }
  t.after(async () => {
    app.kill();
    server.close();
    await new Promise(r => setTimeout(r, 1000));
    await fs.rm(data, { recursive: true, force: true }).catch(() => {});
  });
  return { url: `http://127.0.0.1:${server.address().port}/page.html`, connection };
}

function runAgent(connection, source) {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [path.join(root, 'native', 'agent.mjs')], {
      env: { ...process.env, PLUCK_NATIVE_CONNECTION: connection }, stdio: ['pipe', 'pipe', 'pipe'],
    });
    let out = '', err = '';
    child.stdout.on('data', d => { out += d; });
    child.stderr.on('data', d => { err += d; });
    child.on('error', reject);
    child.on('close', code => code === 0 ? resolve(JSON.parse(out)) : reject(new Error(err)));
    child.stdin.end(source);
  });
}

test('agent input is trusted and snapshots describe controls', { skip: !(await fs.access(exe).then(() => true, () => false)) && 'build native first' }, async t => {
  const { url, connection } = await start(t);
  const r = await runAgent(connection, `
    const t = await browser.session('input test');
    const { id } = await t.open(${JSON.stringify(url)});
    await t.wait(id, 'text=Press me');
    const out = {};
    const snap = await t.snapshot(id);
    out.roles = Object.fromEntries(snap.elements.map(e => [e.text, e.role]));
    out.headings = snap.headings;
    out.click = await t.click(id, 'text=Press me');
    out.fill = await t.fill(id, '#i', '홍길동 hello');
    out.value = await t.js(id, "document.getElementById('i').value");
    await t.press(id, 'Enter');
    out.editor = await t.fill(id, '#ed', 'editor text');
    out.select = await t.select(id, '#s', 'Banana');
    await t.hover(id, '#hov');
    out.menu = await t.js(id, "getComputedStyle(document.getElementById('menu')).display");
    await t.click(id, '#k'); await t.type(id, 'xy'); await t.press(id, 'Control+A'); await t.press(id, 'Backspace');
    out.cleared = await t.js(id, "document.getElementById('k').value");
    const frame = (await t.snapshot(id)).elements.find(e => e.text === 'Frame button');
    out.frame = await t.click(id, frame.ref);
    const quiet = await t.snapshot(id, { diff: true });
    await t.fill(id, '#i', 'changed');
    const changed = await t.snapshot(id, { diff: true });
    out.quiet = { n: quiet.elements.length, text: quiet.text };
    out.changed = changed.elements.map(e => e.value);
    out.log = await t.js(id, 'window.__log');
    console.log(JSON.stringify(out));`);
  assert.equal(r.roles['Press me'], 'button');
  assert.equal(r.roles['Your name'], 'textbox');
  assert.deepEqual(r.headings, ['1 Input test']);
  for (const key of ['click', 'fill', 'editor', 'frame']) assert.equal(r[key].input, 'trusted', key);
  assert.equal(r.value, '홍길동 hello');
  assert.equal(r.select.selected, 'Banana');
  assert.equal(r.menu, 'block');
  assert.equal(r.cleared, '');
  assert.deepEqual(r.quiet, { n: 0, text: null });
  assert.deepEqual(r.changed, ['changed']);
  // Every event the page saw came from real input, not script-made events.
  assert.ok(r.log.length >= 8 && r.log.every(entry => entry.endsWith(':true')), r.log.join(', '));
  for (const kind of ['click', 'input', 'submit', 'hover', 'key:a+ctrl', 'key:Backspace', 'frameclick'])
    assert.ok(r.log.some(entry => entry.startsWith(kind + ':')), kind);
});

test('MCP server lists tools and drives a tab', { skip: !(await fs.access(exe).then(() => true, () => false)) && 'build native first' }, async t => {
  const { url, connection } = await start(t);
  const child = spawn(process.execPath, [path.join(root, 'native', 'mcp.mjs')], {
    env: { ...process.env, PLUCK_NATIVE_CONNECTION: connection }, stdio: ['pipe', 'pipe', 'inherit'],
  });
  const waiting = new Map(); let n = 0;
  readline.createInterface({ input: child.stdout }).on('line', line => { const m = JSON.parse(line); waiting.get(m.id)?.(m); });
  const rpc = (method, params) => new Promise(resolve => {
    const id = ++n; waiting.set(id, resolve);
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  });
  const call = async (name, args) => (await rpc('tools/call', { name, arguments: args })).result;
  const init = await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'test', version: '1' } });
  assert.equal(init.result.serverInfo.name, 'pluck');
  const names = (await rpc('tools/list', {})).result.tools.map(tool => tool.name);
  for (const name of ['open', 'snapshot', 'click', 'fill', 'press', 'screenshot']) assert.ok(names.includes(name), name);
  const id = JSON.parse((await call('open', { url })).content[0].text).id;
  await call('wait', { id, target: 'text=Press me' });
  assert.equal(JSON.parse((await call('click', { id, target: 'text=Press me' })).content[0].text).input, 'trusted');
  const shot = (await call('screenshot', { id, annotate: true })).content[0];
  assert.equal(shot.type, 'image');
  assert.ok(shot.data.length > 1000);
  const missing = await call('click', { id, target: '#nope' });
  assert.equal(missing.isError, true);
  assert.match(missing.content[0].text, /Element not found/);
  const closed = new Promise(resolve => child.on('close', resolve));
  child.stdin.end();
  assert.equal(await closed, 0);
});
