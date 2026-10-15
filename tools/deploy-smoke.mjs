#!/usr/bin/env node
// STORY-045 deployment smoke test (DEC-049): builds and starts the production-like stack as an
// isolated Compose project on a spare port, then checks through nginx that the web app, the API and
// the WebSocket hub answer. It creates a guest, recreates every container (volumes kept) and checks
// the session and its player survived. Finally it removes the project and its volumes. It does not
// touch a running deployment or the dev stack.
import { execFileSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { request } from 'node:http';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const composeFile = join(dirname(fileURLToPath(import.meta.url)), '..', 'infrastructure', 'production', 'docker-compose.yml');
const port = process.env.SMOKE_HTTP_PORT ?? '18081';
const origin = `http://localhost:${port}`;
const env = {
  ...process.env,
  COMPOSE_PROJECT_NAME: `chog-deploy-smoke-${process.pid}`,
  POSTGRES_PASSWORD: randomBytes(16).toString('hex'),
  WALLET_AUTH_DOMAIN: `localhost:${port}`,
  WALLET_AUTH_URI: origin,
  HTTP_BIND: '127.0.0.1',
  HTTP_PORT: port,
  IMAGE_TAG: 'smoke',
};
const compose = (...args) =>
  execFileSync('docker', ['compose', '-f', composeFile, '--env-file', '/dev/null', ...args], {
    env,
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
    maxBuffer: 64 * 1024 * 1024,
  }).trim();
const step = (message) => console.log(`- ${message}`);
const check = (condition, message) => {
  if (!condition) throw new Error(message);
};

async function get(path, cookie) {
  const response = await fetch(`${origin}${path}`, { headers: cookie ? { cookie } : {} });
  return { status: response.status, headers: response.headers, body: await response.text() };
}

// A raw WebSocket handshake: Node's fetch can't upgrade, and the hub accepts WebSockets only.
const upgrade = (path, cookie) =>
  new Promise((resolve, reject) => {
    const req = request(`${origin}${path}`, {
      headers: {
        cookie,
        connection: 'Upgrade',
        upgrade: 'websocket',
        'sec-websocket-version': '13',
        'sec-websocket-key': randomBytes(16).toString('base64'),
      },
    });
    req.on('upgrade', (response, socket) => {
      socket.destroy();
      resolve(response.statusCode);
    });
    req.on('response', (response) => {
      response.resume();
      resolve(response.statusCode);
    });
    req.on('error', reject);
    req.end();
  });

async function main() {
  step('building images and starting the stack (waits for healthy)');
  compose('up', '-d', '--build', '--wait');
  check(compose('ps', '-a', '--format', '{{.Service}} {{.State}} {{.ExitCode}}').includes('migrate exited 0'), 'migrations did not complete');

  step('web app, SPA fallback and API answer through nginx');
  const page = await get('/');
  check(page.status === 200 && page.body.includes('<app-root>'), `GET / returned ${page.status}`);
  check(page.headers.get('cache-control') === 'no-cache', 'index.html must be revalidated');
  const deepLink = await get('/rooms/ABCDEF');
  check(deepLink.status === 200 && deepLink.body.includes('<app-root>'), `deep link returned ${deepLink.status}`);
  const games = await get('/api/games');
  check(games.status === 200 && JSON.parse(games.body).some((g) => g.id === 'chog-life'), `GET /api/games returned ${games.status}`);
  check(!page.headers.get('server')?.match(/\d/), 'nginx must not reveal its version');

  step('unauthenticated API calls are refused');
  check((await get('/api/me')).status === 401, 'GET /api/me without a session must be 401');

  step('creating a guest');
  const created = await fetch(`${origin}/api/guest-sessions`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ displayName: 'Smoke Chog' }),
  });
  check(created.status === 201, `POST /api/guest-sessions returned ${created.status}`);
  const setCookie = created.headers.get('set-cookie') ?? '';
  check(/;\s*secure/i.test(setCookie) && /;\s*httponly/i.test(setCookie), 'session cookie must be Secure and HttpOnly in Production');
  const cookie = setCookie.split(';')[0];
  const player = JSON.parse(await created.text());

  step('the room hub accepts a WebSocket upgrade through nginx');
  const hub = await upgrade('/api/hubs/rooms', cookie);
  check(hub === 101, `hub upgrade returned ${hub}`);

  step('recreating every container (volumes kept)');
  compose('down');
  compose('up', '-d', '--wait');

  const me = await get('/api/me', cookie);
  check(me.status === 200, `GET /api/me after restart returned ${me.status}`);
  const after = JSON.parse(me.body);
  check(after.playerId === player.playerId && after.displayName === 'Smoke Chog', 'the player changed across the restart');
  step('the session and player survived the restart');
  console.log('Deployment smoke test passed.');
}

try {
  await main();
} catch (error) {
  console.error(`Deployment smoke test failed: ${error.message}`);
  try {
    console.error(compose('logs', '--no-color', '--tail', '80'));
  } catch {
    // The stack may not have started at all.
  }
  process.exitCode = 1;
} finally {
  compose('down', '-v', '--remove-orphans');
}
