import { test } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';

test('runner cleans up on success/error, preserves explicit continuation, and isolates runs', async () => {
  const calls = [];
  const server = http.createServer(async (req, res) => {
    let input = '';
    for await (const chunk of req) input += chunk;
    const body = JSON.parse(input);
    calls.push(body);
    res.setHeader('content-type', 'application/json');
    res.end(JSON.stringify(body.command === 'new' ? { id: 42 } : { ok: true }));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'ego-cleanup-'));
  const connection = path.join(dir, 'connection.json');
  await fs.writeFile(connection, JSON.stringify({ port: server.address().port, token: 'test' }));
  const run = (source, args = []) => new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [path.resolve('native/agent.mjs'), ...args], {
      env: { ...process.env, EGO_NATIVE_CONNECTION: connection }, stdio: ['pipe', 'pipe', 'pipe'],
    });
    child.on('error', reject);
    child.on('close', code => resolve(code));
    child.stdin.end(source);
    child.stdout.resume();
    child.stderr.resume();
  });
  const source = "const task = await browser.useOrCreateSpace('test'); await task.open('http://localhost/');";
  try {
    assert.equal(await run(source), 0);
    const first = calls.find(c => c.command === 'new').owner;
    assert.equal(calls.at(-1).command, 'cleanup-run');
    assert.equal(calls.at(-1).owner, first);
    calls.length = 0;
    assert.notEqual(await run(source + "throw new Error('expected');"), 0);
    assert.equal(calls.at(-1).command, 'cleanup-run');
    assert.notEqual(calls.at(-1).owner, first);
    calls.length = 0;
    assert.equal(await run(source, ['--keep-tabs']), 0);
    assert.equal(calls.some(c => c.command === 'cleanup-run'), false);
    calls.length = 0;
    assert.notEqual(await run(source + "throw new Error('expected');", ['--keep-tabs']), 0);
    assert.equal(calls.at(-1).command, 'cleanup-run');
  } finally {
    await new Promise(resolve => server.close(resolve));
    await fs.rm(dir, { recursive: true, force: true });
  }
});
