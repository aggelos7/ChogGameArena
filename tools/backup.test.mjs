// STORY-047 encrypted backups (DEC-051): checks the backup service's resolved configuration, its image
// and script, and the retention rule, without building or starting anything. Compose checks are skipped
// when the Docker CLI is unavailable; `npm run backup:verify` backs up and restores a real stack.
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
const BACKUP = {
  ...REQUIRED,
  COMPOSE_PROFILES: 'backup',
  BACKUP_AGE_RECIPIENT: 'age1testrecipient',
  BACKUP_S3_BUCKET: 'chog-backups',
  BACKUP_S3_ENDPOINT: 'https://s3.example.com',
  BACKUP_S3_ACCESS_KEY_ID: 'test-key-id',
  BACKUP_S3_SECRET_ACCESS_KEY: 'test-secret',
};

const config = (env) => {
  const result = spawnSync('docker', ['compose', '-f', composeFile, '--env-file', '/dev/null', 'config', '--format', 'json'], {
    encoding: 'utf8',
    env: { PATH: process.env.PATH, HOME: process.env.HOME, DOCKER_HOST: process.env.DOCKER_HOST ?? '', ...env },
  });
  assert.equal(result.status, 0, result.stderr);
  return JSON.parse(result.stdout);
};

test('the backup service runs only in the backup profile, which the VM template turns on', { skip }, () => {
  assert.equal(config(REQUIRED).services.backup, undefined);
  assert.ok(config(BACKUP).services.backup);
  const profiles = read(production, '.env.example').match(/^COMPOSE_PROFILES=(.*)$/m)?.[1].split(',');
  assert.deepEqual(profiles, ['letsencrypt', 'backup']);
});

test('the backup service reaches only PostgreSQL, keeps archives in a volume and restarts', { skip }, () => {
  const { services, volumes } = config(BACKUP);
  const backup = services.backup;
  assert.equal(backup.ports, undefined, 'the backup service must not publish a port');
  assert.deepEqual(Object.keys(backup.networks), ['backend']);
  assert.deepEqual(backup.volumes.map(({ type, source, target }) => ({ type, source, target })), [{ type: 'volume', source: 'backups', target: '/backups' }]);
  assert.ok('backups' in volumes);
  assert.equal(backup.restart, 'unless-stopped');
  assert.equal(backup.depends_on.postgres.condition, 'service_healthy');
  assert.deepEqual(backup.healthcheck.test, ['CMD', 'test', '-f', '/tmp/chog-backup-ready'], 'healthy only once the settings check out');
  const script = read(production, 'backup.sh');
  assert.match(script, /^ready=\/tmp\/chog-backup-ready$/m);
  const run = script.slice(script.indexOf('run() {'), script.indexOf('\n}', script.indexOf('run() {')));
  const steps = ['rm -f "$ready"', 'require_recipient', 'require_remote', 'rclone lsf', 'touch "$ready"'].map((step) => run.indexOf(step));
  assert.ok(steps.every((index, i) => index >= 0 && (i === 0 || index > steps[i - 1])), `scheduler start-up out of order: ${steps}`);
  assert.equal(backup.build.dockerfile, 'infrastructure/production/backup.Dockerfile');
  for (const name of ['postgres', 'migrate', 'api']) {
    assert.ok(!services[name].volumes?.some((v) => v.source === 'backups'), `${name} must not see the archives`);
  }
});

test('the backup service takes its settings from the environment, with daily 03:00 UTC and 7 + 4 kept', { skip }, () => {
  const env = config(BACKUP).services.backup.environment;
  assert.deepEqual(
    { host: env.PGHOST, db: env.PGDATABASE, user: env.PGUSER, password: env.PGPASSWORD },
    { host: 'postgres', db: 'choggames', user: 'choggames', password: 'test-only' },
  );
  assert.equal(env.BACKUP_AGE_RECIPIENT, 'age1testrecipient');
  assert.equal(env.BACKUP_TIME, '03:00');
  assert.equal(env.BACKUP_KEEP_DAILY, '7');
  assert.equal(env.BACKUP_KEEP_WEEKLY, '4');
  assert.equal(env.BACKUP_S3_BUCKET, 'chog-backups');
  assert.equal(env.BACKUP_S3_PREFIX, 'chog-game-arena');
  assert.equal(env.RCLONE_CONFIG_OFFSITE_TYPE, 's3');
  assert.equal(env.RCLONE_CONFIG_OFFSITE_PROVIDER, 'Other');
  assert.equal(env.RCLONE_CONFIG_OFFSITE_ENDPOINT, 'https://s3.example.com');
  assert.equal(env.RCLONE_CONFIG_OFFSITE_ACCESS_KEY_ID, 'test-key-id');
  assert.equal(env.RCLONE_CONFIG_OFFSITE_SECRET_ACCESS_KEY, 'test-secret');
  // Without the settings the service still resolves, and the script refuses to back up (backup:verify).
  const bare = config({ ...REQUIRED, COMPOSE_PROFILES: 'backup' }).services.backup.environment;
  assert.equal(bare.BACKUP_AGE_RECIPIENT, '');
  assert.equal(bare.BACKUP_S3_BUCKET, '');
  // No other service gets the bucket key.
  for (const [name, service] of Object.entries(config(BACKUP).services)) {
    if (name !== 'backup') assert.ok(!JSON.stringify(service.environment ?? {}).includes('test-secret'), name);
  }
});

test('the backup image matches the database version, pins its tools and runs as postgres', () => {
  const dockerfile = read(production, 'backup.Dockerfile');
  const base = dockerfile.match(/^ARG POSTGRES=(\S+)$/m)?.[1];
  assert.equal(base, read(composeFile).match(/^\s+image: (postgres:\S+)$/m)?.[1], 'pg_dump must match the server');
  assert.match(dockerfile, /apk add --no-cache age=\d+\.\d+\.\d+-r\d+ rclone=\d+\.\d+\.\d+-r\d+/);
  assert.match(dockerfile, /^USER postgres$/m);
  assert.match(dockerfile, /^ENTRYPOINT \["\/usr\/local\/bin\/chog-backup"\]$/m);
  assert.doesNotMatch(dockerfile, /PASSWORD=|SECRET_ACCESS_KEY|AGE-SECRET-KEY|COPY .*\.env\b/);
  const ignored = read(root, '.dockerignore').split('\n');
  for (const file of ['backup.sh', 'backup-retention.sh']) {
    assert.ok(ignored.includes(`!infrastructure/production/${file}`), `${file} must be in the build context`);
    assert.ok(dockerfile.includes(`infrastructure/production/${file}`), `${file} must be copied into the image`);
  }
});

test('backups are encrypted on the fly, copied off-VM, and pruned only after the copy', () => {
  const script = read(production, 'backup.sh');
  assert.match(script, /^set -eu -o pipefail$/m);
  assert.match(script, /^umask 077$/m);
  assert.match(script, /pg_dump --format=custom [^|\n]*"\$PGDATABASE" \| age --encrypt --recipient "\$BACKUP_AGE_RECIPIENT"/, 'the plain dump must never be written to disk');
  assert.doesNotMatch(script, /pg_dumpall|--file[= ]/, 'only the database, never roles or a plain file');
  const backup = script.slice(script.indexOf('backup() {'), script.indexOf('\n}', script.indexOf('backup() {')));
  const order = ['require_recipient', 'require_remote', 'pg_dump', 'age-encryption.org/v1', 'mv "$partial"', 'rclone copyto', 'prune'].map((step) => backup.indexOf(step));
  assert.ok(order.every((index, i) => index >= 0 && (i === 0 || index > order[i - 1])), `backup steps out of order: ${order}`);
});

test('restore reads the key from standard input and changes only an empty database, in one transaction', () => {
  const script = read(production, 'backup.sh');
  const restore = script.slice(script.indexOf('restore() {'), script.indexOf('\n}', script.indexOf('restore() {')));
  assert.match(restore, /cat >"\$work\/identity"/);
  assert.match(restore, /\[ "\$relations" = 0 \] \|\| fail/);
  assert.match(restore, /case "\$wanted" in \*\/\* \| \.\*\) fail/, 'archive names must not be paths');
  assert.match(restore, /pg_restore [^\n]*--single-transaction --exit-on-error/);
  const steps = ['user_relations', 'age --decrypt', 'pg_restore --list', 'pg_restore --no-password'].map((step) => restore.indexOf(step));
  assert.ok(steps.every((index, i) => index >= 0 && (i === 0 || index > steps[i - 1])), `restore steps out of order: ${steps}`);
});

test('the env template holds backup placeholders only, never a private key', () => {
  const example = read(production, '.env.example');
  assert.match(example, /^BACKUP_AGE_RECIPIENT=age1replace-with-your-public-key$/m);
  assert.match(example, /^BACKUP_S3_SECRET_ACCESS_KEY=change-me$/m);
  assert.doesNotMatch(example, /AGE-SECRET-KEY-1/);
});

// The retention rule, run with the host's sh and awk (the image uses BusyBox; backup:verify runs it there).
const retain = (names, ...args) => {
  const result = spawnSync('sh', [join(production, 'backup-retention.sh'), ...args], { input: names.join('\n'), encoding: 'utf8' });
  return { status: result.status, deleted: result.stdout.split('\n').filter(Boolean).sort(), stderr: result.stderr };
};
const daily = (last, count, time = '030000') =>
  Array.from({ length: count }, (_, i) => {
    const day = new Date(Date.UTC(...last) - i * 86_400_000).toISOString().slice(0, 10).replaceAll('-', '');
    return `choggames-${day}T${time}Z.dump.age`;
  });
const day = (name) => name.slice(10, 18);

test('retention keeps the 7 newest and the newest of each of the 4 newest weeks', () => {
  // 41 daily archives up to Thursday 2026-10-15.
  const names = daily([2026, 9, 15], 41);
  const { status, deleted } = retain(names);
  assert.equal(status, 0);
  const kept = names.filter((n) => !deleted.includes(n)).map(day).sort();
  // 7 newest (10-09..10-15); weeks starting 10-12, 10-05, 09-28 and 09-21 keep 10-15, 10-11, 10-04 and 09-27.
  assert.deepEqual(kept, ['20260927', '20261004', '20261009', '20261010', '20261011', '20261012', '20261013', '20261014', '20261015']);
});

test('retention deletes nothing while there are few archives, and never other files', () => {
  assert.deepEqual(retain(daily([2026, 9, 15], 7)).deleted, []);
  const others = ['notes.txt', '.choggames-20200101T030000Z.dump.age.partial', 'choggames-20200101T030000Z.dump', 'choggames-2020-01-01.dump.age'];
  assert.deepEqual(retain([...others, ...daily([2026, 9, 15], 3)], '0', '0').deleted, daily([2026, 9, 15], 3).sort());
});

test('retention keeps the newest archive of a week, whatever the input order or gaps', () => {
  // Two a day for three days in one week, then one on a Sunday three weeks earlier.
  const names = [
    'choggames-20261014T150000Z.dump.age',
    'choggames-20261013T030000Z.dump.age',
    'choggames-20261014T030000Z.dump.age',
    'choggames-20260920T030000Z.dump.age',
    'choggames-20261013T150000Z.dump.age',
    'choggames-20261012T030000Z.dump.age',
  ];
  const { deleted } = retain(names, '1', '2');
  assert.deepEqual(deleted, ['choggames-20261012T030000Z.dump.age', 'choggames-20261013T030000Z.dump.age', 'choggames-20261013T150000Z.dump.age', 'choggames-20261014T030000Z.dump.age']);
  assert.deepEqual(retain(names, '0', '1').deleted.length, 5, 'one week kept: only its newest archive');
});

test('retention refuses counts that are not whole numbers', () => {
  const result = retain(daily([2026, 9, 15], 10), 'seven');
  assert.equal(result.status, 2);
  assert.match(result.stderr, /whole numbers/);
  assert.deepEqual(result.deleted, []);
});
