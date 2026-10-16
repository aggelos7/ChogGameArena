#!/usr/bin/env node
// STORY-045/046/048 deployment smoke test (DEC-049, DEC-050, DEC-052): builds and starts the production-like stack
// as an isolated Compose project on spare ports, then checks through nginx, over TLS verified against
// the certificate nginx serves, that the web app, the API and the WebSocket hub (wss) answer with the
// HTTPS headers, that plain HTTP redirects and other host names are refused. It installs a stand-in
// "issued" certificate where certbot would write one and checks nginx switches to it. It creates a
// guest, checks readiness is public while metrics stay internal, that the API returns the request id nginx
// logged and logs JSON lines, recreates every container (volumes kept) and checks the session, its player and the
// certificate survived. In between it runs the release smoke test (STORY-049, DEC-053) through nginx over TLS and
// wss: guests, rooms, matches played to the finish and a reconnect. Finally it removes the project and its volumes. It does not touch a running
// deployment or the dev stack, and it never contacts Let's Encrypt (the letsencrypt profile is off).
import { execFileSync } from 'node:child_process';
import { X509Certificate, randomBytes } from 'node:crypto';
import { request as httpRequest } from 'node:http';
import { request as httpsRequest } from 'node:https';
import { connect } from 'node:tls';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { runReleaseSmoke } from './release-smoke.mjs';

const composeFile = join(dirname(fileURLToPath(import.meta.url)), '..', 'infrastructure', 'production', 'docker-compose.yml');
const hostname = 'localhost';
const httpPort = process.env.SMOKE_HTTP_PORT ?? '18081';
const httpsPort = process.env.SMOKE_HTTPS_PORT ?? '18443';
const origin = `https://${hostname}:${httpsPort}`;
const project = `chog-deploy-smoke-${process.pid}`;
const env = {
  ...process.env,
  COMPOSE_PROJECT_NAME: project,
  COMPOSE_PROFILES: '',
  POSTGRES_PASSWORD: randomBytes(16).toString('hex'),
  PUBLIC_HOSTNAME: hostname,
  // The wallet sign-in domain carries the non-standard port; on the VM it defaults to PUBLIC_HOSTNAME.
  WALLET_AUTH_DOMAIN: `${hostname}:${httpsPort}`,
  WALLET_AUTH_URI: origin,
  HTTP_BIND: '127.0.0.1',
  HTTP_PORT: httpPort,
  HTTPS_PORT: httpsPort,
  IMAGE_TAG: 'smoke',
  // The release smoke test plays both games, which production lists as Coming Soon by default.
  CHOG_LIFE_STATUS: 'Available',
  CHOG_RACE_STATUS: 'Available',
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

// Every HTTPS request trusts exactly the certificate nginx serves (read from a handshake, below) and
// checks it names the host, so TLS is really verified.
let ca;

// One request on a fresh connection (no keep-alive, so a recreated nginx needs no retry dance).
const send = (url, { method = 'GET', headers = {}, body, servername = hostname } = {}) =>
  new Promise((resolve, reject) => {
    const tls = url.startsWith('https:');
    const req = (tls ? httpsRequest : httpRequest)(url, { method, headers, agent: false, ...(tls ? { ca, servername } : {}) }, (response) => {
      let text = '';
      response.setEncoding('utf8');
      response.on('data', (chunk) => (text += chunk));
      response.on('end', () => resolve({ status: response.statusCode, headers: response.headers, body: text }));
    });
    req.on('error', reject);
    req.end(body);
  });

const get = (path, cookie) => send(`${origin}${path}`, { headers: cookie ? { cookie } : {} });

async function retry(attempt, seconds = 60) {
  const deadline = Date.now() + seconds * 1000;
  for (;;) {
    try {
      return await attempt();
    } catch (error) {
      if (Date.now() > deadline) throw error;
      await new Promise((resolve) => setTimeout(resolve, 1000));
    }
  }
}

// A raw WebSocket handshake over TLS (wss): the hub accepts WebSockets only.
const upgrade = (path, cookie) =>
  new Promise((resolve, reject) => {
    const req = httpsRequest(`${origin}${path}`, {
      ca,
      agent: false,
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

// A TLS handshake for a host name, unverified: 'refused', or the certificate nginx presented (PEM).
// Asking nginx, not reading its files, is what tells when a reload has taken effect.
const handshake = (servername) =>
  new Promise((resolve) => {
    const socket = connect({ host: '127.0.0.1', port: Number(httpsPort), servername, rejectUnauthorized: false });
    socket.on('secureConnect', () => {
      const certificate = socket.getPeerX509Certificate().toString();
      socket.destroy();
      resolve(certificate);
    });
    socket.on('error', () => resolve('refused'));
  });
const servedCertificate = () => handshake(hostname);

const fingerprint = (pem) => new X509Certificate(pem).fingerprint256;

// A stand-in for the certificate certbot would obtain, written into the letsencrypt volume the way
// certbot lays it out (live/chog links to archive/chog).
const installIssuedCertificate = () =>
  execFileSync('docker', ['run', '--rm', '-v', `${project}_letsencrypt:/etc/letsencrypt`, '--entrypoint', 'sh', `${env.IMAGE_PREFIX ?? 'chog-game-arena'}/web:smoke`, '-c', [
    'set -e',
    'mkdir -p /etc/letsencrypt/archive/chog /etc/letsencrypt/live/chog',
    `openssl req -x509 -newkey rsa:2048 -nodes -days 2 -subj /CN=${hostname} -addext subjectAltName=DNS:${hostname} -keyout /etc/letsencrypt/archive/chog/privkey1.pem -out /etc/letsencrypt/archive/chog/fullchain1.pem 2>/dev/null`,
    'ln -sf ../../archive/chog/fullchain1.pem /etc/letsencrypt/live/chog/fullchain.pem',
    'ln -sf ../../archive/chog/privkey1.pem /etc/letsencrypt/live/chog/privkey.pem',
    'cat /etc/letsencrypt/archive/chog/fullchain1.pem',
  ].join('\n')], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();

async function main() {
  step('building images and starting the stack (waits for healthy)');
  compose('up', '-d', '--build', '--wait');
  check(compose('ps', '-a', '--format', '{{.Service}} {{.State}} {{.ExitCode}}').includes('migrate exited 0'), 'migrations did not complete');
  ca = await servedCertificate();
  check(new X509Certificate(ca).checkHost(hostname), 'the start-up certificate must name PUBLIC_HOSTNAME');

  step('web app, SPA fallback and API answer through nginx over HTTPS');
  const page = await get('/');
  check(page.status === 200 && page.body.includes('<app-root>'), `GET / returned ${page.status}`);
  check(page.headers['cache-control'] === 'no-cache', 'index.html must be revalidated');
  const deepLink = await get('/rooms/ABCDEF');
  check(deepLink.status === 200 && deepLink.body.includes('<app-root>'), `deep link returned ${deepLink.status}`);
  const games = await get('/api/games');
  check(games.status === 200 && JSON.parse(games.body).some((g) => g.id === 'chog-life'), `GET /api/games returned ${games.status}`);
  check(!page.headers.server?.match(/\d/), 'nginx must not reveal its version');
  check(!/http:\/\//.test(page.body), 'index.html must not load anything over plain http');

  step('every HTTPS response carries HSTS and upgrade-insecure-requests');
  const bundle = page.body.match(/src="(main-[A-Za-z0-9]+\.js)"/)?.[1];
  check(bundle, 'index.html must load a hashed main bundle');
  for (const [path, response] of [['/', page], ['/api/games', games], [`/${bundle}`, await get(`/${bundle}`)]]) {
    check(response.headers['strict-transport-security'] === 'max-age=31536000', `${path} must send HSTS for one year`);
    check(response.headers['content-security-policy'] === 'upgrade-insecure-requests', `${path} must send upgrade-insecure-requests`);
    check(response.headers['x-content-type-options'] === 'nosniff', `${path} must send nosniff`);
  }

  step('plain HTTP redirects to HTTPS, except the ACME challenge');
  const http = `http://${hostname}:${httpPort}`;
  const redirect = await send(`${http}/rooms/ABCDEF?x=1`);
  check(redirect.status === 301 && redirect.headers.location === `https://${hostname}/rooms/ABCDEF?x=1`, `HTTP returned ${redirect.status} ${redirect.headers.location}`);
  const challenge = await send(`${http}/.well-known/acme-challenge/missing-token`);
  check(challenge.status === 404, `the ACME challenge path returned ${challenge.status}, not a redirect`);
  check(!('strict-transport-security' in redirect.headers), 'HSTS is sent over HTTPS only');

  step('other host names are refused');
  check((await handshake('other.example.com')) === 'refused', 'TLS for another host name must be refused');
  check((await handshake(hostname)) !== 'refused', 'TLS for PUBLIC_HOSTNAME must be accepted');
  const otherHost = await send(`${http}/`, { headers: { host: 'other.example.com' } }).then(
    (response) => response.status,
    () => 'closed',
  );
  check(otherHost === 'closed', `HTTP for another host name returned ${otherHost}`);

  step('unauthenticated API calls are refused');
  check((await get('/api/me')).status === 401, 'GET /api/me without a session must be 401');

  step('creating a guest');
  const created = await send(`${origin}/api/guest-sessions`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ displayName: 'Smoke Chog' }),
  });
  check(created.status === 201, `POST /api/guest-sessions returned ${created.status}`);
  const setCookie = [created.headers['set-cookie'] ?? []].flat().join('\n');
  check(/;\s*secure/i.test(setCookie) && /;\s*httponly/i.test(setCookie), 'session cookie must be Secure and HttpOnly in Production');
  const cookie = setCookie.split(';')[0];
  const player = JSON.parse(created.body);

  step('the room hub accepts a WebSocket upgrade over wss');
  const hub = await upgrade('/api/hubs/rooms', cookie);
  check(hub === 101, `hub upgrade returned ${hub}`);

  step('readiness is public; metrics and liveness are not (they get the web app)');
  const ready = await get('/api/health/ready');
  check(ready.status === 200 && JSON.parse(ready.body).status === 'Healthy', `GET /api/health/ready returned ${ready.status}`);
  for (const path of ['/metrics', '/health/live', '/health/ready']) {
    const internal = await get(path);
    check(internal.body.includes('<app-root>') && !internal.body.includes('# TYPE'), `${path} must not reach the API`);
  }

  step('nginx sets and logs every request id, the API returns it, and API logs are JSON without secrets');
  const traced = await send(`${origin}/api/games`, { headers: { 'x-request-id': 'chosen-by-the-client' } });
  const requestId = traced.headers['x-request-id'];
  check(/^[0-9a-f]{32}$/.test(requestId ?? ''), `the request id must be nginx's, not ${requestId}`);
  const accessLog = compose('logs', '--no-log-prefix', '--no-color', 'web').split('\n').filter((line) => line.startsWith('{'));
  const entry = accessLog.map((line) => JSON.parse(line)).find((logged) => logged.requestId === requestId);
  check(entry?.path === '/api/games' && entry.status === 200, 'the access log must hold the request with its id');
  const apiLog = compose('logs', '--no-log-prefix', '--no-color', 'api').split('\n').filter(Boolean);
  check(apiLog.length > 0 && apiLog.every((line) => JSON.parse(line).Timestamp && JSON.parse(line).Category), 'API logs must be JSON lines');
  check(!apiLog.join('\n').includes(cookie.split('=')[1]), 'the session token must never be logged');

  step('metrics are readable inside the network');
  const metrics = compose('exec', '-T', 'api', 'bash', '-c',
    "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /metrics HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && cat <&3");
  check(/^choggames_guest_sessions_created_total 1$/m.test(metrics), 'metrics must count the guest');
  check(/^http_server_request_duration_seconds_count\{[^}]*http_route="\/api\/games"[^}]*\} \d+$/m.test(metrics), 'metrics must time requests by route');

  step('release smoke test through nginx (TLS and wss)');
  await runReleaseSmoke({ origin, ca, log: (line) => console.log(`  ${line}`) });

  step('nginx switches to the issued certificate once it appears (checked every minute)');
  const issued = installIssuedCertificate();
  check(fingerprint(issued) !== fingerprint(ca), 'the stand-in certificate must differ from the start-up one');
  await retry(async () => check(fingerprint(await servedCertificate()) === fingerprint(issued), 'not switched yet'), 90);
  ca = issued;
  // Old workers finish gracefully after a reload, so one more connection may still meet the old one.
  check((await retry(() => get('/api/games'), 10)).status === 200, 'HTTPS must work with the issued certificate');

  step('recreating every container (volumes kept)');
  compose('down');
  compose('up', '-d', '--wait');

  const me = await retry(() => get('/api/me', cookie));
  check(me.status === 200, `GET /api/me after restart returned ${me.status}`);
  const after = JSON.parse(me.body);
  check(after.playerId === player.playerId && after.displayName === 'Smoke Chog', 'the player changed across the restart');
  step('the session, the player and the issued certificate survived the restart');
  console.log('Deployment smoke test passed.');
}

try {
  await main();
} catch (error) {
  const cause = error.cause ? ` (${error.cause.code ?? error.cause.message})` : '';
  console.error(`Deployment smoke test failed: ${error.message}${cause}`);
  try {
    console.error(compose('logs', '--no-color', '--tail', '80'));
  } catch {
    // The stack may not have started at all.
  }
  process.exitCode = 1;
} finally {
  compose('down', '-v', '--remove-orphans');
}
