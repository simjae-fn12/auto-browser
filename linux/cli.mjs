#!/usr/bin/env node
// Linux counterpart of native/cli.ps1: one agent command per call against a running Oinzi.
// Run `session` once, then export OINZI_SESSION=<the id it prints> so later commands act in that session.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const [command, arg1, arg2, arg3] = process.argv.slice(2);
const usage = `Usage: cli.mjs <command> [args]
  session [label] | active | list | save | new [url] | show <id> | close <id>
  snapshot <id> [full|diff] | screenshot <id> [file.png] [annotate]
  goto <id> <url> | click <id> <target> | fill <id> <target> <text> | hover <id> <target>
  type <id> <text> | select <id> <target> <option> | clickAt <id> <x> <y>
  press <id> <key> | scroll <id> <up|down> [pixels] | back <id> | forward <id>
  wait <id> <target> [ms] | js <id> <code>`;
if (!command || command === 'help' || command === '--help') { console.log(usage); process.exit(0); }

const connectionPath = process.env.OINZI_CONNECTION
  || path.join(process.env.XDG_CONFIG_HOME || path.join(os.homedir(), '.config'), 'Oinzi', 'connection.json');
let connection;
try { connection = JSON.parse(fs.readFileSync(connectionPath, 'utf8')); }
catch { console.error('Oinzi를 먼저 실행하세요.'); process.exit(1); }

const id = Number(arg1);
const body = { command, session: process.env.OINZI_SESSION || '' };
switch (command) {
  case 'session': body.label = arg1; break;
  case 'active': case 'list': case 'save': break;
  case 'new': body.url = arg1; break;
  case 'show': case 'close': case 'back': case 'forward': body.id = id; break;
  case 'snapshot':
    body.id = id;
    if (arg2 === 'full') { body.textLimit = 20000; body.elementLimit = 250; }
    if (arg2 === 'diff') body.diff = true;
    break;
  case 'screenshot': body.id = id; if (arg3 === 'annotate') body.annotate = true; break;
  case 'goto': body.id = id; body.url = arg2; break;
  case 'click': case 'hover': body.id = id; body.target = arg2; break;
  case 'fill': case 'select': body.id = id; body.target = arg2; body.value = arg3 ?? ''; break;
  case 'type': body.id = id; body.value = arg2 ?? ''; break;
  case 'clickAt': body.id = id; body.x = Number(arg2); body.y = Number(arg3); break;
  case 'press': body.id = id; body.key = arg2; break;
  case 'scroll': body.id = id; body.direction = arg2; body.pixels = Number(arg3) || 600; break;
  case 'wait': body.id = id; body.target = arg2; body.timeout = Number(arg3) || 10000; break;
  case 'js': body.id = id; body.code = arg2; break;
  default: console.error(`Unknown command: ${command}\n\n${usage}`); process.exit(1);
}

try {
  const response = await fetch(`http://127.0.0.1:${connection.port}/`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', authorization: `Bearer ${connection.token}` },
    body: JSON.stringify(body),
  });
  const result = await response.json();
  if (command === 'screenshot' && response.ok) {
    const file = path.resolve(arg2 || `tab-${arg1}.png`);
    fs.writeFileSync(file, Buffer.from(result.png, 'base64'));
    console.log(file);
  } else console.log(JSON.stringify(result, null, 2));
  if (!response.ok) process.exitCode = 1;
} catch (error) {
  console.error(`브라우저에 연결하지 못했습니다: ${error.message}`);
  process.exitCode = 1;
}
