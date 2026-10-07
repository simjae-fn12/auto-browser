import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const [command, ...args] = process.argv.slice(2);
if (!command || command === 'help') {
  console.log('Usage: node cli.mjs list | new [url] | show <id> | goto <id> <url> | snapshot <id> | click <id> <@ref|css> | fill <id> <@ref|css> <text> | wait <id> <css> [ms] | js <id> <code> | screenshot <id> <file.png> | close <id>');
  process.exit(0);
}

const file = process.env.EGO_WINDOWS_CONNECTION || path.join(process.env.APPDATA || path.join(os.homedir(), 'AppData', 'Roaming'), 'ego-windows', 'connection.json');
let connection;
try { connection = JSON.parse(fs.readFileSync(file, 'utf8')); }
catch { console.error('Ego Windows가 실행 중이 아닙니다. npm start로 먼저 실행하세요.'); process.exit(1); }

const [id, target, value] = args;
const body = { command };
if (command === 'new') body.url = id;
else if (command === 'show' || command === 'snapshot' || command === 'close' || command === 'screenshot') body.id = Number(id);
else if (command === 'goto') { body.id = Number(id); body.url = target; }
else if (command === 'click') { body.id = Number(id); body.target = target; }
else if (command === 'fill') { body.id = Number(id); body.target = target; body.value = value ?? ''; }
else if (command === 'wait') { body.id = Number(id); body.target = target; body.timeout = Number(value) || 10000; }
else if (command === 'js') { body.id = Number(id); body.code = args.slice(1).join(' '); }

try {
  const response = await fetch(`http://127.0.0.1:${connection.port}/`, {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${connection.token}` },
    body: JSON.stringify(body),
  });
  const result = await response.json();
  if (command === 'screenshot' && response.ok) {
    const file = path.resolve(target || `tab-${id}.png`);
    fs.writeFileSync(file, Buffer.from(result.png, 'base64'));
    console.log(file);
  } else console.log(JSON.stringify(result, null, 2));
  if (!response.ok) process.exitCode = 1;
} catch (error) {
  console.error(`브라우저에 연결하지 못했습니다: ${error.message}`);
  process.exitCode = 1;
}
