// STORY-048 operational monitoring (DEC-052): checks the log rotation, the proxy's request ids and access log,
// what nginx exposes of the API's health and metrics, and the backup heartbeat, without building or starting
// anything. Compose checks are skipped when the Docker CLI is unavailable; `npm run deploy:verify` and
// `npm run backup:verify` check them on a running stack.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const production = join(root, 'infrastructure', 'production');
const composeFile = join(production, 'docker-compose.yml');
const read = (...path) => readFileSync(join(...path), 'utf8');
const hasDocker = spawnSync('docker', ['compose', 'version'], { encoding: 'utf8' }).status === 0;
const skip = hasDocker ? false : 'Docker Compose CLI not available';

const ALL = { POSTGRES_PASSWORD: 'test-only', PUBLIC_HOSTNAME: 'app.example.com', COMPOSE_PROFILES: 'letsencrypt,backup' };

const config = (env) => {
  const result = spawnSync('docker', ['compose', '-f', composeFile, '--env-file', '/dev/null', 'config', '--format', 'json'], {
    encoding: 'utf8',
    env: { PATH: process.env.PATH, HOME: process.env.HOME, DOCKER_HOST: process.env.DOCKER_HOST ?? '', ...env },
  });
  assert.equal(result.status, 0, result.stderr);
  return JSON.parse(result.stdout);
};

// A location's body ends at the line holding only its closing brace.
const locations = (nginx) =>
  Object.fromEntries([...nginx.matchAll(/location ([^{\n]+)\{([\s\S]*?)\n\s*\}/g)].map(([, path, body]) => [path.trim(), body]));

test('every service logs to rotated json-file logs', { skip }, () => {
  const { services } = config(ALL);
  assert.deepEqual(Object.keys(services).sort(), ['api', 'backup', 'certbot', 'migrate', 'postgres', 'redis', 'web']);
  for (const [name, service] of Object.entries(services)) {
    assert.deepEqual(service.logging, { driver: 'json-file', options: { 'max-size': '10m', 'max-file': '5' } }, name);
  }
});

test('the API logs JSON lines with scopes and UTC timestamps outside development', () => {
  const settings = JSON.parse(read(root, 'backend', 'src', 'ChogGames.Api', 'appsettings.json')).Logging;
  assert.equal(settings.Console.FormatterName, 'json');
  assert.equal(settings.Console.FormatterOptions.IncludeScopes, true);
  assert.equal(settings.Console.FormatterOptions.UseUtcTimestamp, true);
  assert.equal(settings.LogLevel['Microsoft.EntityFrameworkCore.Database.Command'], 'Warning', 'no SQL text per query');
});

test('nginx gives every API request an id and logs it, never query strings or headers', () => {
  const nginx = read(production, 'nginx.conf.template');
  const format = nginx.match(/log_format chog_json escape=json ([\s\S]*?);\n/)?.[1] ?? '';
  assert.match(format, /"requestId":"\$request_id"/);
  assert.match(format, /"path":"\$uri"/);
  assert.doesNotMatch(format, /\$request_uri|\$args|\$query_string|\$http_cookie|\$http_authorization|\$request_body/);
  assert.equal([...nginx.matchAll(/access_log \/var\/log\/nginx\/access\.log chog_json;/g)].length, 2, 'both public servers use it');
  for (const [path, body] of Object.entries(locations(nginx))) {
    if (body.includes('proxy_pass')) assert.match(body, /proxy_set_header X-Request-Id \$request_id;/, `${path} must overwrite the request id`);
  }
});

test('nginx exposes the API readiness only; liveness and metrics stay internal', () => {
  const proxied = Object.entries(locations(read(production, 'nginx.conf.template'))).filter(([, body]) => body.includes('proxy_pass'));
  assert.deepEqual(proxied.map(([path]) => path).sort(), ['/api/', '/api/hubs/', '= /api/health/ready', '= /api/profile/avatar-image']);
  const ready = Object.fromEntries(proxied)['= /api/health/ready'];
  assert.match(ready, /proxy_pass \$api\/health\/ready;/);
  assert.match(ready, /proxy_set_header X-Forwarded-For \$remote_addr;/);
  for (const [path, body] of proxied) {
    if (path !== '= /api/health/ready') assert.match(body, /proxy_pass \$api;/, `${path} forwards its own path only`);
  }
});

test('the backup service gets the heartbeat URLs, and the script pings without logging them', { skip }, () => {
  const env = config({ ...ALL, BACKUP_HEARTBEAT_URL: 'https://hc.example.com/ping/abc', BACKUP_HEARTBEAT_FAIL_URL: 'https://hc.example.com/ping/abc/fail' })
    .services.backup.environment;
  assert.equal(env.BACKUP_HEARTBEAT_URL, 'https://hc.example.com/ping/abc');
  assert.equal(env.BACKUP_HEARTBEAT_FAIL_URL, 'https://hc.example.com/ping/abc/fail');
  const bare = config(ALL).services.backup.environment;
  assert.equal(bare.BACKUP_HEARTBEAT_URL, '');
  assert.equal(bare.BACKUP_HEARTBEAT_FAIL_URL, '');
});

test('the backup script pings after the off-VM copy and on a failed scheduled backup', () => {
  const script = read(production, 'backup.sh');
  const backup = script.slice(script.indexOf('backup() {'), script.indexOf('\n}', script.indexOf('backup() {')));
  assert.ok(backup.indexOf('ping_heartbeat BACKUP_HEARTBEAT_URL') > backup.indexOf('rclone copyto'), 'only a backup copied off-VM counts');
  assert.match(script, /log "backup failed; retrying in \$\{wait_seconds\}s"\n\s+ping_heartbeat BACKUP_HEARTBEAT_FAIL_URL/);
  const ping = script.slice(script.indexOf('ping_heartbeat() {'), script.indexOf('\n}', script.indexOf('ping_heartbeat() {')));
  assert.match(ping, /wget -q -T 15 -O \/dev\/null "\$url" 2>\/dev\/null/);
  assert.doesNotMatch(ping, /log [^\n]*\$url/, 'the URL carries the monitor token');
  assert.match(read(production, '.env.example'), /^# BACKUP_HEARTBEAT_URL=$/m, 'the template leaves the heartbeat off');
});
