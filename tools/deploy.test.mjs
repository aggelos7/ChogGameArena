// STORY-045 production-like Compose deployment (DEC-049): validates the resolved configuration and
// the image/proxy files without building or starting anything. Compose checks are skipped when the
// Docker CLI is unavailable; `npm run deploy:verify` builds and runs the stack.
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

const REQUIRED = { POSTGRES_PASSWORD: 'test-only', WALLET_AUTH_DOMAIN: 'app.example.com', WALLET_AUTH_URI: 'https://app.example.com' };

// Resolve config with explicit test values only, ignoring any local infrastructure/production/.env.
const resolveConfig = (env) =>
  spawnSync('docker', ['compose', '-f', composeFile, '--env-file', '/dev/null', 'config', '--format', 'json'], {
    encoding: 'utf8',
    env: { PATH: process.env.PATH, HOME: process.env.HOME, DOCKER_HOST: process.env.DOCKER_HOST ?? '', ...env },
  });

const config = (env = REQUIRED) => {
  const result = resolveConfig(env);
  assert.equal(result.status, 0, result.stderr);
  return JSON.parse(result.stdout);
};

const LONG_RUNNING = ['api', 'postgres', 'redis', 'web'];

test('defines the web, api, migrate, postgres and redis services', { skip }, () => {
  assert.deepEqual(Object.keys(config().services).sort(), ['api', 'migrate', 'postgres', 'redis', 'web']);
});

test('only nginx publishes a port, on loopback 8080 by default', { skip }, () => {
  const { services } = config();
  for (const [name, service] of Object.entries(services)) {
    if (name !== 'web') assert.equal(service.ports, undefined, `${name} must not publish a port`);
  }
  assert.deepEqual(
    services.web.ports.map(({ host_ip, published, target }) => ({ host_ip, published, target })),
    [{ host_ip: '127.0.0.1', published: '8080', target: 80 }],
  );
  const exposed = config({ ...REQUIRED, HTTP_BIND: '0.0.0.0', HTTP_PORT: '80' }).services.web.ports[0];
  assert.equal(exposed.host_ip, '0.0.0.0');
  assert.equal(exposed.published, '80');
});

test('database and Redis data live in named volumes', { skip }, () => {
  const { services, volumes } = config();
  const expected = { postgres: ['postgres-data', '/var/lib/postgresql'], redis: ['redis-data', '/data'] };
  for (const [name, [volume, target]] of Object.entries(expected)) {
    const mount = services[name].volumes.find((v) => v.target === target);
    assert.equal(mount?.type, 'volume', `${name} must mount a named volume at ${target}`);
    assert.equal(mount.source, volume);
    assert.ok(volume in volumes, `volume ${volume} must be declared`);
  }
});

test('long-running services restart; the migration job runs once', { skip }, () => {
  const { services } = config();
  for (const name of LONG_RUNNING) assert.equal(services[name].restart, 'unless-stopped', name);
  assert.equal(services.migrate.restart, 'no');
});

test('the API starts only after migrations succeed, and nginx only once the API is healthy', { skip }, () => {
  const { services } = config();
  assert.equal(services.migrate.depends_on.postgres.condition, 'service_healthy');
  assert.equal(services.api.depends_on.migrate.condition, 'service_completed_successfully');
  assert.equal(services.api.depends_on.postgres.condition, 'service_healthy');
  assert.equal(services.web.depends_on.api.condition, 'service_healthy');
  for (const name of ['postgres', 'redis']) assert.ok(services[name].healthcheck?.test?.length, `${name} needs a healthcheck`);
});

test('the API runs in Production behind the proxy with settings from the environment', { skip }, () => {
  const env = config().services.api.environment;
  assert.equal(env.ASPNETCORE_ENVIRONMENT, 'Production');
  assert.equal(env.ASPNETCORE_FORWARDEDHEADERS_ENABLED, 'true');
  assert.equal(env.Database__ConnectionString, 'Host=postgres;Port=5432;Database=choggames;Username=choggames;Password=test-only');
  assert.equal(env.WalletAuth__Domain, 'app.example.com');
  assert.equal(env.WalletAuth__Uri, 'https://app.example.com');
  assert.equal(env.Games__Items__0__Status, 'ComingSoon');
  assert.equal(config().services.migrate.environment.Database__ConnectionString, env.Database__ConnectionString);
});

for (const missing of Object.keys(REQUIRED)) {
  test(`missing ${missing} fails before anything starts`, { skip }, () => {
    const { [missing]: _, ...env } = REQUIRED;
    const result = resolveConfig(env);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, new RegExp(`${missing} must be set`));
  });
}

test('base images are pinned to exact versions', () => {
  const images = [
    ...['api.Dockerfile', 'web.Dockerfile'].flatMap((file) => [...read(production, file).matchAll(/^ARG \w+=(\S+)$/gm)].map((m) => m[1])),
    ...[...read(composeFile).matchAll(/^\s+image: ([^$\s]+)$/gm)].map((m) => m[1]),
  ];
  assert.equal(images.length, 6, images.join(', '));
  for (const image of images) assert.match(image, /:\d+\.\d+(\.\d+)?(-[a-z0-9.-]+)?$/, `${image} must be pinned`);
});

test('runtime images run as a non-root user and build no secrets in', () => {
  const api = read(production, 'api.Dockerfile');
  assert.equal([...api.matchAll(/^USER \$APP_UID$/gm)].length, 2, 'api and migrate must drop root');
  for (const file of ['api.Dockerfile', 'web.Dockerfile']) {
    assert.doesNotMatch(read(production, file), /ConnectionString=|PASSWORD=|COPY .*\.env\b/i, file);
  }
  const ignored = read(root, '.dockerignore').split('\n');
  for (const pattern of ['.git', '**/.env', '**/.env.*', '**/node_modules', '**/bin', '**/obj']) assert.ok(ignored.includes(pattern), pattern);
});

test('nginx serves the app and proxies the API and its WebSocket hub on one origin', () => {
  const nginx = read(production, 'nginx.conf');
  assert.match(nginx, /resolver 127\.0\.0\.11 /, 'the API address must be re-resolved after it is recreated');
  assert.match(nginx, /set \$api http:\/\/api:8080;/);
  // proxy_set_header isn't inherited into a location that sets its own, so each API location must set it.
  for (const location of ['/api/hubs/', '/api/']) {
    const block = nginx.match(new RegExp(`location ${location.replaceAll('/', '\\/')} \\{([^}]*)\\}`))?.[1] ?? '';
    assert.match(block, /proxy_set_header X-Forwarded-For \$remote_addr;/, `${location} must overwrite, never append, the client address`);
    assert.match(block, /proxy_set_header X-Forwarded-Proto \$scheme;/, location);
  }
  assert.doesNotMatch(nginx, /\$proxy_add_x_forwarded_for/);
  assert.match(nginx, /location \/api\/hubs\/ \{[^}]*proxy_set_header Upgrade \$http_upgrade;[^}]*proxy_set_header Connection \$connection_upgrade;/s);
  assert.match(nginx, /location \/api\/ \{[^}]*proxy_pass \$api;/s);
  assert.match(nginx, /try_files \$uri \$uri\/ \/index\.html;/);
  assert.match(nginx, /server_tokens off;/);
});

test('the env template holds placeholders only', () => {
  const example = read(production, '.env.example');
  assert.match(example, /^POSTGRES_PASSWORD=change-me-local-only$/m);
  assert.doesNotMatch(read(composeFile), /^\s+POSTGRES_PASSWORD:\s*(?!\$\{)\S/m);
});
