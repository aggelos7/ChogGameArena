// STORY-004 Docker Compose checks: validates the resolved configuration (no containers
// are started). Skipped when the Docker CLI is unavailable; `npm run deps:verify` runs
// the runtime restart/persistence smoke test.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const composeFile = join(root, 'infrastructure', 'docker-compose.yml');
const hasDocker = spawnSync('docker', ['compose', 'version'], { encoding: 'utf8' }).status === 0;
const skip = hasDocker ? false : 'Docker Compose CLI not available';

// Resolve config with explicit test values only, ignoring any local infrastructure/.env.
const resolveConfig = (env) =>
  spawnSync('docker', ['compose', '-f', composeFile, '--env-file', '/dev/null', 'config', '--format', 'json'], {
    encoding: 'utf8',
    env: { PATH: process.env.PATH, HOME: process.env.HOME, DOCKER_HOST: process.env.DOCKER_HOST ?? '', ...env },
  });

const config = () => {
  const result = resolveConfig({ POSTGRES_PASSWORD: 'test-only' });
  assert.equal(result.status, 0, result.stderr);
  return JSON.parse(result.stdout);
};

test('defines exactly postgres and redis services', { skip }, () => {
  assert.deepEqual(Object.keys(config().services).sort(), ['postgres', 'redis']);
});

test('images are pinned to explicit versions', { skip }, () => {
  for (const [name, service] of Object.entries(config().services)) {
    assert.match(service.image, /^[a-z]+:\d+\.\d+(\.\d+)?-alpine[\d.]*$/, `${name} image must be pinned, got ${service.image}`);
  }
});

test('data is stored in named volumes', { skip }, () => {
  const { services, volumes } = config();
  const expected = { postgres: ['postgres-data', '/var/lib/postgresql'], redis: ['redis-data', '/data'] };
  for (const [name, [volume, target]] of Object.entries(expected)) {
    const mount = services[name].volumes.find((v) => v.target === target);
    assert.equal(mount?.type, 'volume', `${name} must mount a named volume at ${target}`);
    assert.equal(mount.source, volume);
    assert.ok(volume in volumes, `volume ${volume} must be declared`);
  }
});

test('ports are published on localhost only', { skip }, () => {
  for (const [name, service] of Object.entries(config().services)) {
    assert.ok(service.ports?.length, `${name} must publish a port`);
    for (const port of service.ports) assert.equal(port.host_ip, '127.0.0.1', `${name} must bind to 127.0.0.1`);
  }
});

test('ports can be overridden to avoid local conflicts', { skip }, () => {
  const result = resolveConfig({ POSTGRES_PASSWORD: 'test-only', POSTGRES_PORT: '15432', REDIS_PORT: '16379' });
  const { services } = JSON.parse(result.stdout);
  assert.equal(services.postgres.ports[0].published, '15432');
  assert.equal(services.redis.ports[0].published, '16379');
});

test('services define health checks and restart policy', { skip }, () => {
  for (const [name, service] of Object.entries(config().services)) {
    assert.ok(service.healthcheck?.test?.length, `${name} must define a healthcheck`);
    assert.equal(service.restart, 'unless-stopped');
  }
});

test('missing POSTGRES_PASSWORD fails before anything starts', { skip }, () => {
  const result = resolveConfig({});
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /POSTGRES_PASSWORD must be set/);
});

test('compose file and env template contain no real secrets', () => {
  // The password may only come from interpolation, never a literal value.
  assert.doesNotMatch(readFileSync(composeFile, 'utf8'), /^\s+POSTGRES_PASSWORD:\s*(?!\$\{)\S/m);
  const example = readFileSync(join(root, 'infrastructure', '.env.example'), 'utf8');
  assert.match(example, /^POSTGRES_PASSWORD=change-me-local-only$/m);
});
