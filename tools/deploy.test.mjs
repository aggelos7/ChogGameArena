// STORY-045/046 production-like Compose deployment with HTTPS (DEC-049, DEC-050): validates the
// resolved configuration and the image/proxy files without building or starting anything. Compose checks are skipped when the
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

const REQUIRED = { POSTGRES_PASSWORD: 'test-only', PUBLIC_HOSTNAME: 'app.example.com' };
const WITH_CERTBOT = { ...REQUIRED, COMPOSE_PROFILES: 'letsencrypt' };

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

test('defines the web, api, migrate, postgres and redis services, and certbot in the letsencrypt profile', { skip }, () => {
  assert.deepEqual(Object.keys(config().services).sort(), ['api', 'migrate', 'postgres', 'redis', 'web']);
  assert.deepEqual(Object.keys(config(WITH_CERTBOT).services).sort(), ['api', 'certbot', 'migrate', 'postgres', 'redis', 'web']);
  assert.match(read(production, '.env.example'), /^COMPOSE_PROFILES=letsencrypt(,|$)/m, 'the VM template turns certbot on');
});

test('only nginx publishes ports: 80 and 443 on every IPv4 and IPv6 address by default', { skip }, () => {
  const { services } = config(WITH_CERTBOT);
  for (const [name, service] of Object.entries(services)) {
    if (name !== 'web') assert.equal(service.ports, undefined, `${name} must not publish a port`);
  }
  const ports = (env) => config(env).services.web.ports.map(({ host_ip, published, target }) => ({ host_ip, published, target }));
  assert.deepEqual(ports(REQUIRED), [
    { host_ip: '0.0.0.0', published: '80', target: 80 },
    { host_ip: '0.0.0.0', published: '443', target: 443 },
    { host_ip: '::', published: '80', target: 80 },
    { host_ip: '::', published: '443', target: 443 },
  ]);
  // A local stack (release checklist, smoke tests) binds both families to loopback and nothing else.
  const local = { ...REQUIRED, HTTP_BIND: '127.0.0.1', HTTP_BIND_V6: '::1', HTTP_PORT: '18081', HTTPS_PORT: '18443' };
  assert.deepEqual(ports(local), [
    { host_ip: '127.0.0.1', published: '18081', target: 80 },
    { host_ip: '127.0.0.1', published: '18443', target: 443 },
    { host_ip: '::1', published: '18081', target: 80 },
    { host_ip: '::1', published: '18443', target: 443 },
  ]);
  assert.equal(config().networks.backend.enable_ipv6, true, 'IPv6 clients must reach nginx with their own address, not through a proxy');
  // HTTP_BIND_V6 equal to HTTP_BIND collapses into one binding: IPv4 only, for a machine without IPv6.
  assert.deepEqual(ports({ ...local, HTTP_BIND_V6: '127.0.0.1' }), [
    { host_ip: '127.0.0.1', published: '18081', target: 80 },
    { host_ip: '127.0.0.1', published: '18443', target: 443 },
  ]);
  assert.match(read(root, 'docs', 'release-checklist.md'), /`HTTP_BIND` \/ `HTTP_BIND_V6` \/ `HTTP_PORT` \/ `HTTPS_PORT` \| `127\.0\.0\.1` \/ `::1` \//, 'the local release stack binds IPv6 to loopback too');
});

test('certbot alone writes the certificate; nginx reads it and the challenge tokens', { skip }, () => {
  const { services, volumes } = config(WITH_CERTBOT);
  const mounts = (name) => Object.fromEntries(services[name].volumes.map((v) => [v.target, { source: v.source, readOnly: v.read_only === true }]));
  assert.deepEqual(mounts('certbot'), {
    '/etc/letsencrypt': { source: 'letsencrypt', readOnly: false },
    '/var/www/certbot': { source: 'acme-webroot', readOnly: false },
  });
  assert.deepEqual(mounts('web'), {
    '/etc/letsencrypt': { source: 'letsencrypt', readOnly: true },
    '/var/www/certbot': { source: 'acme-webroot', readOnly: true },
  });
  for (const volume of ['letsencrypt', 'acme-webroot']) assert.ok(volume in volumes, volume);
  assert.equal(services.web.environment.PUBLIC_HOSTNAME, 'app.example.com');
  assert.equal(services.certbot.environment.PUBLIC_HOSTNAME, 'app.example.com');
  assert.equal(services.certbot.depends_on.web.condition, 'service_healthy');
  assert.equal(services.certbot.networks?.backend, undefined, 'certbot needs no access to the API or the database');
});

test('certbot obtains and renews one certificate for the public host through the webroot', { skip }, () => {
  const script = config(WITH_CERTBOT).services.certbot.command.join(' ');
  for (const part of ['certbot certonly', '--webroot -w /var/www/certbot', '--cert-name chog', '-d "$$PUBLIC_HOSTNAME"', '--non-interactive', '--keep-until-expiring']) {
    assert.ok(script.includes(part), part);
  }
  assert.doesNotMatch(script, /--staging|--force-renewal|--dry-run/);
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
  assert.equal(config(WITH_CERTBOT).services.certbot.restart, 'unless-stopped');
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
  assert.equal(env.WalletAuth__Domain, 'app.example.com', 'the wallet sign-in domain defaults to the public host');
  assert.equal(env.WalletAuth__Uri, 'https://app.example.com');
  const smoke = config({ ...REQUIRED, WALLET_AUTH_DOMAIN: 'localhost:18443', WALLET_AUTH_URI: 'https://localhost:18443' }).services.api.environment;
  assert.equal(smoke.WalletAuth__Domain, 'localhost:18443');
  assert.equal(smoke.WalletAuth__Uri, 'https://localhost:18443');
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
  assert.equal(images.length, 7, images.join(', '));
  for (const image of images) assert.match(image, /:v?\d+\.\d+(\.\d+)?(-[a-z0-9.-]+)?$/, `${image} must be pinned`);
  assert.ok(images.includes('certbot/certbot:v5.8.0'));
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

// The server block for the public host on a port, without its nested location blocks' bodies.
const serverBlock = (nginx, port) => {
  const start = nginx.search(new RegExp(`server \\{\\s*listen ${port}( ssl)?;\\s*listen \\[::\\]:${port}( ssl)?;[^}]*?server_name \\$\\{PUBLIC_HOSTNAME\\};`));
  assert.ok(start >= 0, `no server for PUBLIC_HOSTNAME on ${port}`);
  let depth = 0;
  for (let i = nginx.indexOf('{', start); i < nginx.length; i++) {
    if (nginx[i] === '{') depth++;
    if (nginx[i] === '}' && --depth === 0) return nginx.slice(start, i + 1);
  }
  assert.fail('unbalanced server block');
};
// A location's body ends at the line holding only its closing brace (a body may contain ${...}).
const LOCATION = /location ([^{\n]+)\{([\s\S]*?)\n\s*\}/g;
const locations = (block) => [...block.matchAll(LOCATION)].map(([, path, body]) => ({ path: path.trim(), body }));

test('nginx serves the app and proxies the API and its WebSocket hub on one origin', () => {
  const nginx = read(production, 'nginx.conf.template');
  assert.match(nginx, /resolver 127\.0\.0\.11 /, 'the API address must be re-resolved after it is recreated');
  assert.match(nginx, /set \$api http:\/\/api:8080;/);
  // proxy_set_header isn't inherited into a location that sets its own, so each API location must set it.
  for (const location of ['/api/hubs/', '/api/', '= /api/profile/avatar-image']) {
    const block = nginx.match(new RegExp(`location ${location.replaceAll('/', '\\/')} \\{([^}]*)\\}`))?.[1] ?? '';
    assert.match(block, /proxy_set_header X-Forwarded-For \$remote_addr;/, `${location} must overwrite, never append, the client address`);
    assert.match(block, /proxy_set_header X-Forwarded-Proto \$scheme;/, location);
  }
  assert.doesNotMatch(nginx, /\$proxy_add_x_forwarded_for/);
  assert.match(nginx, /location \/api\/hubs\/ \{[^}]*proxy_set_header Upgrade \$http_upgrade;[^}]*proxy_set_header Connection \$connection_upgrade;/s);
  assert.match(nginx, /location \/api\/ \{[^}]*proxy_pass \$api;/s);
  assert.match(nginx, /client_max_body_size 64k;/, 'small bodies everywhere else');
  // Photo uploads only (STORY-062): the API caps them at AvatarImages:MaxUploadBytes, 2 MB by default.
  assert.match(nginx, /location = \/api\/profile\/avatar-image \{[^}]*client_max_body_size 2m;[^}]*proxy_pass \$api;/s);
  assert.match(nginx, /try_files \$uri \$uri\/ \/index\.html;/);
  assert.match(nginx, /server_tokens off;/);
  const web = read(production, 'web.Dockerfile');
  assert.match(web, /COPY infrastructure\/production\/nginx\.conf\.template \/etc\/nginx\/templates\/default\.conf\.template/);
  assert.match(web, /^ENV NGINX_ENVSUBST_FILTER=\^PUBLIC_HOSTNAME\$$/m, 'only PUBLIC_HOSTNAME may be substituted, never nginx variables');
  const ignored = read(root, '.dockerignore').split('\n');
  for (const file of ['nginx.conf.template', 'security-headers.conf', 'nginx-tls.sh']) {
    assert.ok(ignored.includes(`!infrastructure/production/${file}`), `${file} must be in the build context`);
    assert.ok(web.includes(`infrastructure/production/${file}`), `${file} must be copied into the image`);
  }
});

test('nginx serves only the public host, and only over TLS 1.2+', () => {
  const nginx = read(production, 'nginx.conf.template');
  assert.match(nginx, /server \{\s*listen 80 default_server;\s*listen \[::\]:80 default_server;\s*return 444;\s*\}/, 'other hosts over HTTP get no answer');
  assert.match(nginx, /server \{\s*listen 443 ssl default_server;\s*listen \[::\]:443 ssl default_server;\s*ssl_reject_handshake on;\s*\}/, 'other hosts over TLS get no certificate');
  // Every public listen exists for IPv4 and IPv6 alike (STORY-087), so neither family falls through to another server.
  const listens = [...nginx.matchAll(/^\s*listen (.+);$/gm)].map(([, value]) => value).filter((value) => !value.startsWith('127.0.0.1:'));
  assert.deepEqual(listens.filter((value) => value.startsWith('[::]:')).map((value) => value.slice(5)), listens.filter((value) => !value.startsWith('[')));
  const https = serverBlock(nginx, 443);
  assert.match(https, /ssl_certificate\s+\/etc\/nginx\/tls\/current\/fullchain\.pem;/);
  assert.match(https, /ssl_certificate_key \/etc\/nginx\/tls\/current\/privkey\.pem;/);
  assert.match(https, /ssl_protocols TLSv1\.2 TLSv1\.3;/);
  assert.match(https, /ssl_session_tickets off;/);
  assert.match(https, /location \/api\/hubs\//, 'the hub is proxied over TLS (wss)');
  assert.doesNotMatch(nginx, /\$\{(?!PUBLIC_HOSTNAME\})/, 'PUBLIC_HOSTNAME is the only template variable');
});

test('plain HTTP serves the ACME challenge and redirects everything else to HTTPS', () => {
  const http = locations(serverBlock(read(production, 'nginx.conf.template'), 80));
  assert.deepEqual(http.map((l) => l.path), ['/.well-known/acme-challenge/', '/']);
  assert.match(http[0].body, /root \/var\/www\/certbot;/);
  assert.match(http[1].body, /return 301 https:\/\/\$\{PUBLIC_HOSTNAME\}\$request_uri;/, 'redirect to the public host, never the client-sent Host');
  assert.doesNotMatch(serverBlock(read(production, 'nginx.conf.template'), 80), /proxy_pass|Strict-Transport-Security/);
});

test('every HTTPS response sends HSTS for one year and upgrade-insecure-requests', () => {
  const headers = read(production, 'security-headers.conf');
  assert.match(headers, /^add_header Strict-Transport-Security "max-age=31536000" always;$/m, 'one year, no includeSubDomains or preload');
  assert.match(headers, /^add_header Content-Security-Policy "upgrade-insecure-requests" always;$/m);
  for (const header of ['X-Content-Type-Options nosniff', 'X-Frame-Options DENY', 'Referrer-Policy same-origin']) assert.ok(headers.includes(`add_header ${header} always;`), header);
  const https = serverBlock(read(production, 'nginx.conf.template'), 443);
  const include = 'include /etc/nginx/snippets/security-headers.conf;';
  assert.ok(https.replace(LOCATION, '').includes(include), 'the server sends them');
  // A location with its own add_header inherits none from the server, so it must include them again.
  for (const { path, body } of locations(https)) {
    if (body.includes('add_header')) assert.ok(body.includes(include), `${path} drops the security headers`);
  }
});

test('nginx starts on a self-signed certificate and switches to the issued one', () => {
  const script = read(production, 'nginx-tls.sh');
  assert.match(script, /^live=\/etc\/letsencrypt\/live\/chog$/m, 'the --cert-name certbot uses');
  assert.match(script, /subjectAltName=DNS:\$\{PUBLIC_HOSTNAME\}/);
  assert.match(script, /readlink -f "\$live\/fullchain\.pem"/, 'a renewal (new archive file) counts as a change');
  assert.match(script, /nginx -s reload/);
  assert.match(read(production, 'web.Dockerfile'), /COPY --chmod=755 infrastructure\/production\/nginx-tls\.sh \/docker-entrypoint\.d\/40-chog-tls\.sh/);
});

test('the env template holds placeholders only', () => {
  const example = read(production, '.env.example');
  assert.match(example, /^POSTGRES_PASSWORD=change-me-local-only$/m);
  assert.doesNotMatch(read(composeFile), /^\s+POSTGRES_PASSWORD:\s*(?!\$\{)\S/m);
});
