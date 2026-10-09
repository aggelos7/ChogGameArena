// STORY-001 workspace skeleton checks. Uses only Node built-ins (node:test) so it
// runs right after `npm ci` with no extra dependencies.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync, statSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const read = (p) => readFileSync(join(root, p), 'utf8');
const readJson = (p) => JSON.parse(read(p));
const EXACT_VERSION = /^\d+\.\d+\.\d+$/;

test('expected top-level directories exist', () => {
  for (const dir of ['apps', 'libs', 'backend', 'infrastructure', 'docs', 'tools']) {
    assert.ok(existsSync(join(root, dir)) && statSync(join(root, dir)).isDirectory(), `missing ${dir}/`);
  }
});

test('planning documents are preserved', () => {
  for (const doc of [
    'AGENTS.md',
    'README.md',
    'docs/requirements.md',
    'docs/architecture.md',
    'docs/backlog.md',
    'docs/decisions.md',
  ]) {
    assert.ok(existsSync(join(root, doc)), `missing ${doc}`);
    assert.ok(read(doc).trim().length > 0, `${doc} is empty`);
  }
});

test('Nx is pinned to an exact version and locked', () => {
  const pkg = readJson('package.json');
  const nxVersion = pkg.devDependencies?.nx;
  assert.match(nxVersion ?? '', EXACT_VERSION, 'nx must be pinned to an exact version');

  for (const [name, version] of Object.entries({ ...pkg.dependencies, ...pkg.devDependencies, ...pkg.overrides })) {
    assert.match(version, EXACT_VERSION, `${name} must be pinned exactly, got "${version}"`);
  }

  assert.ok(existsSync(join(root, 'package-lock.json')), 'package-lock.json must be committed');
  const lock = readJson('package-lock.json');
  assert.equal(lock.packages?.['node_modules/nx']?.version, nxVersion, 'lockfile nx version must match package.json');
});

test('Node.js LTS version is pinned and enforced', () => {
  const pkg = readJson('package.json');
  const nvmrcMajor = Number.parseInt(read('.nvmrc').trim(), 10);
  assert.equal(nvmrcMajor % 2, 0, '.nvmrc must reference an even-numbered (LTS) Node.js major');
  assert.match(pkg.engines?.node ?? '', new RegExp(`>=${nvmrcMajor}\\.\\d+\\.\\d+ <${nvmrcMajor + 1}\\.0\\.0`));
  assert.match(read('.npmrc'), /^engine-strict=true$/m, 'npm must reject unsupported Node.js versions');
});

test('Nx Cloud is disabled and local caching is configured', () => {
  const nx = readJson('nx.json');
  assert.equal(nx.neverConnectToCloud, true);
  for (const key of ['nxCloudId', 'nxCloudAccessToken', 'nxCloudUrl']) {
    assert.equal(nx[key], undefined, `nx.json must not set ${key}`);
  }
  assert.equal(nx.cacheDirectory, '.nx/cache');
});

test('local caches, build output and secrets are git-ignored', () => {
  const ignored = read('.gitignore').split('\n').map((l) => l.trim());
  for (const entry of ['node_modules/', '.nx/cache/', '.nx/workspace-data/', 'dist/', '.env', '.env.*', 'bin/', 'obj/']) {
    assert.ok(ignored.includes(entry), `.gitignore must contain ${entry}`);
  }
  assert.ok(ignored.includes('!.env.example'), '.env.example must remain committable');
});

test('README documents prerequisites and local commands', () => {
  const readme = read('README.md');
  for (const text of ['Node.js 22', 'npm ci', 'npx nx --version', '.NET 8', 'Docker']) {
    assert.ok(readme.includes(text), `README must mention "${text}"`);
  }
});

test('local Nx CLI runs the pinned version without a cloud account', () => {
  const pkg = readJson('package.json');
  const result = spawnSync(join(root, 'node_modules', '.bin', 'nx'), ['--version'], {
    cwd: root,
    encoding: 'utf8',
    env: { ...process.env, NX_NO_CLOUD: 'true', NX_DAEMON: 'false' },
  });
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, new RegExp(`Local: v${pkg.devDependencies.nx.replaceAll('.', '\\.')}`));
});
