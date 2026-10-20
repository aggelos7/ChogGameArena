#!/usr/bin/env node
// STORY-047 backup and restore smoke test (DEC-051). Builds the production-like stack with the `backup`
// profile as an isolated Compose project on spare ports, with a throwaway age key pair and a Docker
// volume standing in for the S3 bucket (rclone's `local` backend; the bucket code path is the same).
// It creates a guest, backs up, and checks the archive is encrypted, holds no secrets, reached the
// off-VM copy and that retention pruned old archives in both places. Then it deletes the whole stack
// with its volumes and restores into a clean one from the off-VM copy, after checking that a wrong
// key, a damaged archive and a non-empty database are all refused without changing anything. The
// guest's session must work again on the restored stack. Finally it removes everything it created.
// It does not touch a running deployment or the dev stack, and never contacts a real bucket.
import { execFileSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { request } from 'node:https';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const retention = join(root, 'infrastructure', 'production', 'backup-retention.sh');
const composeFile = join(root, 'infrastructure', 'production', 'docker-compose.yml');
const httpsPort = process.env.SMOKE_HTTPS_PORT ?? '18443';
const origin = `https://localhost:${httpsPort}`;
const offsiteVolume = `chog-backup-smoke-offsite-${process.pid}`;
const workDir = mkdtempSync(join(tmpdir(), 'chog-backup-smoke-'));
const override = join(workDir, 'offsite.yml');
// The stand-in bucket, mounted where BACKUP_S3_BUCKET points, and a stand-in heartbeat monitor (STORY-048)
// that answers every request and prints its request line.
writeFileSync(
  override,
  `services:
  backup:
    volumes: ["${offsiteVolume}:/offsite"]
  heartbeat:
    image: chog-game-arena/backup:smoke
    entrypoint: ["sh", "-c", "while :; do printf 'HTTP/1.0 200 OK\\r\\nContent-Length: 0\\r\\n\\r\\n' | nc -l -p 8080 | head -n 1; done"]
    networks: [backend]
volumes:
  ${offsiteVolume}:
    external: true
`,
);
const heartbeatToken = randomBytes(12).toString('hex');

const s3Secret = randomBytes(20).toString('hex');
const baseEnv = {
  ...process.env,
  COMPOSE_PROFILES: 'backup',
  POSTGRES_PASSWORD: randomBytes(16).toString('hex'),
  PUBLIC_HOSTNAME: 'localhost',
  WALLET_AUTH_DOMAIN: `localhost:${httpsPort}`,
  WALLET_AUTH_URI: origin,
  HTTP_BIND: '127.0.0.1',
  // IPv4 only: deploy:verify checks IPv6, and this way it runs on machines without it.
  HTTP_BIND_V6: '127.0.0.1',
  HTTP_PORT: process.env.SMOKE_HTTP_PORT ?? '18081',
  HTTPS_PORT: httpsPort,
  IMAGE_TAG: 'smoke',
  BACKUP_REMOTE_TYPE: 'local',
  BACKUP_S3_BUCKET: '/offsite',
  BACKUP_S3_ACCESS_KEY_ID: 'smoke-access-key',
  BACKUP_S3_SECRET_ACCESS_KEY: s3Secret,
};
const projects = { original: `chog-backup-smoke-${process.pid}`, restored: `chog-restore-smoke-${process.pid}` };
let env = { ...baseEnv, COMPOSE_PROJECT_NAME: projects.original };

const run = (file, args, { input, encoding = 'utf8', project = env.COMPOSE_PROJECT_NAME } = {}) =>
  execFileSync(file, args, {
    env: { ...env, COMPOSE_PROJECT_NAME: project },
    input,
    encoding,
    stdio: ['pipe', 'pipe', 'pipe'],
    maxBuffer: 64 * 1024 * 1024,
  });
const compose = (args, options) => {
  const out = run('docker', ['compose', '-f', composeFile, '-f', override, '--env-file', '/dev/null', ...args], options);
  return typeof out === 'string' ? out.trim() : out;
};
// A command that must fail: its stderr and stdout, for checking the reason.
const refused = (args, options) => {
  try {
    compose(args, options);
  } catch (error) {
    return `${error.stdout ?? ''}${error.stderr ?? ''}`;
  }
  throw new Error(`expected to fail: ${args.join(' ')}`);
};
const backupShell = (script, options) => compose(['run', '--rm', '-T', '--entrypoint', 'sh', 'backup', '-c', script], options);
const step = (message) => console.log(`- ${message}`);
const check = (condition, message) => {
  if (!condition) throw new Error(message);
};

// Plain HTTPS against the stack's self-signed certificate: TLS is deploy:verify's subject, not this one's.
const send = (path, { method = 'GET', headers = {}, body } = {}) =>
  new Promise((resolve, reject) => {
    const req = request(`${origin}${path}`, { method, headers, agent: false, rejectUnauthorized: false }, (response) => {
      let text = '';
      response.setEncoding('utf8');
      response.on('data', (chunk) => (text += chunk));
      response.on('end', () => resolve({ status: response.statusCode, headers: response.headers, body: text }));
    });
    req.on('error', reject);
    req.end(body);
  });
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

const relations = () =>
  compose(['exec', '-T', 'postgres', 'sh', '-c',
    `psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg\\_%'"`]);
const listing = (dir) => backupShell(`ls -1 ${dir} | grep '\\.dump\\.age$' || true`).split('\n').filter(Boolean).sort();
const archiveDate = (date) => `choggames-${date.toISOString().slice(0, 10).replaceAll('-', '')}T030000Z.dump.age`;

async function main() {
  run('docker', ['volume', 'create', offsiteVolume]);

  step('building images and creating a throwaway age key pair');
  compose(['build']);
  const identity = run('docker', ['run', '--rm', '--entrypoint', 'age-keygen', 'chog-game-arena/backup:smoke']);
  const recipient = identity.match(/^# public key: (age1[0-9a-z]+)$/m)?.[1];
  check(recipient, 'age-keygen printed no public key');
  const otherIdentity = run('docker', ['run', '--rm', '--entrypoint', 'age-keygen', 'chog-game-arena/backup:smoke']);
  env = { ...env, BACKUP_AGE_RECIPIENT: recipient };
  // A bucket is writable by its key; here the volume must be writable by the backup user.
  run('docker', ['run', '--rm', '--user', 'root', '-v', `${offsiteVolume}:/offsite`, '--entrypoint', 'chown', 'chog-game-arena/backup:smoke', 'postgres:postgres', '/offsite']);

  step('a backup needs an encryption key and a reachable off-VM bucket');
  check(/BACKUP_AGE_RECIPIENT is not set/.test(refused(['run', '--rm', '-T', '-e', 'BACKUP_AGE_RECIPIENT=', 'backup', 'now'])), 'a backup without a recipient must be refused');
  check(/not a valid age public key/.test(refused(['run', '--rm', '-T', '-e', 'BACKUP_AGE_RECIPIENT=age1replace-with-your-public-key', 'backup', 'run'])), 'the template placeholder key must be refused');
  check(/BACKUP_S3_BUCKET is not set/.test(refused(['run', '--rm', '-T', '-e', 'BACKUP_S3_BUCKET=', 'backup', 'now'])), 'a backup without a bucket must be refused');
  check(/BACKUP_TIME must be HH:MM/.test(refused(['run', '--rm', '-T', '-e', 'BACKUP_TIME=25:00', 'backup', 'run'])), 'a bad schedule must be refused');
  const unreachable = ['-e', 'RCLONE_CONFIG_OFFSITE_TYPE=s3', '-e', 'RCLONE_CONFIG_OFFSITE_ENDPOINT=http://127.0.0.1:9'];
  check(/cannot reach the off-VM bucket/.test(refused(['run', '--rm', '-T', ...unreachable, 'backup', 'run'])), 'an unreachable bucket must stop the scheduler');

  step('starting the stack with the backup service (healthy once its key and bucket check out)');
  compose(['up', '-d', '--wait']);
  check(compose(['ps', '--format', '{{.Service}} {{.Health}}']).split('\n').includes('backup healthy'), 'the backup service must be healthy');
  check(/next backup in \d+s \(daily at 03:00 UTC\)/.test(compose(['logs', 'backup'])), 'the backup service must wait for 03:00 UTC');

  step('creating a guest');
  const created = await retry(() =>
    send('/api/guest-sessions', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ displayName: 'Backup Chog' }) }),
  );
  check(created.status === 201, `POST /api/guest-sessions returned ${created.status}`);
  const cookie = [created.headers['set-cookie'] ?? []].flat()[0].split(';')[0];
  const player = JSON.parse(created.body);

  step('older archives on the VM and off-VM, to be pruned');
  // 20 daily archives up to Tuesday 2026-01-20: retention keeps the 7 newest overall (today's and 6 of
  // these) and the newest of each of the 4 newest weeks (today's, 01-20, 01-18 and 01-11).
  const old = Array.from({ length: 20 }, (_, i) => archiveDate(new Date(Date.UTC(2026, 0, 20 - i))));
  backupShell(`set -e; mkdir -p /offsite/chog-game-arena; for f in ${old.join(' ')}; do echo old > /backups/$f; cp /backups/$f /offsite/chog-game-arena/$f; done`);

  step('backing up now');
  const log = compose(['run', '--rm', '-T', '-e', `BACKUP_HEARTBEAT_URL=http://heartbeat:8080/ping/${heartbeatToken}`, 'backup', 'now']);
  const name = log.match(/wrote (choggames-\d{8}T\d{6}Z\.dump\.age)/)?.[1];
  check(name && log.includes(`copied ${name} off-VM`), `backup did not finish:\n${log}`);

  step('a successful backup pings the heartbeat monitor without logging its URL');
  check(log.includes('pinged BACKUP_HEARTBEAT_URL'), `the backup must report the heartbeat ping:\n${log}`);
  check(!log.includes(heartbeatToken), 'the heartbeat URL must not be logged');
  check(compose(['logs', '--no-log-prefix', 'heartbeat']).includes(`GET /ping/${heartbeatToken} `), 'the heartbeat monitor must receive the ping');
  const expected = run('sh', [retention], { input: [name, ...old].join('\n') }).split('\n').filter(Boolean);
  const kept = [name, ...old].filter((archive) => !expected.includes(archive)).sort();
  check(kept.length === 8 && kept.includes(name), `retention should keep 8 archives, not ${kept.join(', ')}`);
  check(JSON.stringify(listing('/backups')) === JSON.stringify(kept), `kept on the VM: ${listing('/backups').join(', ')}`);
  check(JSON.stringify(listing('/offsite/chog-game-arena')) === JSON.stringify(kept), `kept off-VM: ${listing('/offsite/chog-game-arena').join(', ')}`);

  step('the archive is encrypted, identical off-VM, readable only by owner, and holds no secrets');
  const archive = compose(['exec', '-T', 'backup', 'cat', `/backups/${name}`], { encoding: 'buffer' });
  check(archive.subarray(0, 21).toString() === 'age-encryption.org/v1', 'the archive must be age-encrypted');
  check(!archive.includes('Backup Chog'), 'the archive must not hold readable data');
  const offsite = backupShell(`cat /offsite/chog-game-arena/${name}`, { encoding: 'buffer' });
  check(offsite.equals(archive), 'the off-VM copy must be the same archive');
  check(backupShell(`stat -c %a /backups/${name}`) === '600', 'archives must be readable by their owner only');
  const sql = backupShell(`cat > /tmp/identity; age -d -i /tmp/identity /backups/${name} | pg_restore -f -`, { input: identity });
  check(sql.includes('Backup Chog') && sql.includes('__EFMigrationsHistory'), 'the decrypted dump must hold the database');
  for (const [label, secret] of [
    ['the PostgreSQL password', env.POSTGRES_PASSWORD],
    ['the S3 secret key', s3Secret],
    ['the age private key', identity.match(/AGE-SECRET-KEY-\S+/)[0]],
    ['the session token', cookie.split('=')[1]],
  ]) {
    check(!sql.includes(secret), `the archive must not contain ${label}`);
  }
  check(!/CREATE ROLE|ALTER ROLE|PASSWORD/i.test(sql), 'the archive must not contain roles or passwords');

  step('losing the VM: removing the stack and all its volumes (the off-VM copy stays)');
  compose(['down', '-v', '--remove-orphans']);

  step('a clean environment: a new stack with only PostgreSQL running');
  env = { ...env, COMPOSE_PROJECT_NAME: projects.restored };
  compose(['up', '-d', '--wait', 'postgres']);
  check(listing('/backups').length === 0, 'the new VM starts with no local archives');

  step('a wrong key, a damaged archive and a missing key are refused without changing the database');
  check(/cannot decrypt .* the database was not changed/.test(refused(['run', '--rm', '-T', 'backup', 'restore', 'latest'], { input: otherIdentity })), 'a wrong key must be refused');
  const damaged = archiveDate(new Date(Date.now() + 86_400_000));
  // Overwrite one byte of the ciphertext with a different one: writing a fixed 'X' left the archive intact whenever that
  // byte already was one (1 run in 256), and the "damaged" archive then restored.
  backupShell(
    `set -e; f=/backups/${damaged}; cp /offsite/chog-game-arena/${name} "$f"; ` +
      `b=$(dd if="$f" bs=1 skip=300 count=1 2>/dev/null | od -An -tu1 | tr -d ' '); ` +
      `if [ "$b" = 88 ]; then c=Y; else c=X; fi; printf "$c" | dd of="$f" bs=1 seek=300 conv=notrunc 2>/dev/null; ` +
      `cmp -s "$f" /offsite/chog-game-arena/${name} && { echo 'the archive was not changed'; exit 1; } || true`,
  );
  check(/cannot decrypt .* the database was not changed/.test(refused(['run', '--rm', '-T', 'backup', 'restore', damaged], { input: identity })), 'a damaged archive must be refused');
  backupShell(`rm /backups/${damaged}`);
  check(/no age identity on standard input/.test(refused(['run', '--rm', '-T', 'backup', 'restore', 'latest'], { input: '' })), 'a restore without a key must be refused');
  check(/is not a backup archive name/.test(refused(['run', '--rm', '-T', 'backup', 'restore', '../etc/passwd'], { input: identity })), 'a path must be refused');
  check(relations() === '0', 'refused restores must leave the database empty');

  step('restoring the latest archive from the off-VM copy');
  const restored = compose(['run', '--rm', '-T', 'backup', 'restore', 'latest'], { input: identity });
  check(restored.includes(`restoring ${name} from off-VM`) && restored.includes(`restored ${name}`), `restore did not finish:\n${restored}`);
  const tables = relations();
  check(Number(tables) > 0, 'the restored database must have tables');

  step('a second restore into the now non-empty database is refused');
  check(/is not empty/.test(refused(['run', '--rm', '-T', 'backup', 'restore', 'latest'], { input: identity })), 'restoring over data must be refused');
  check(relations() === tables, 'the refused restore must not change the database');

  step('starting the restored stack: migrations have nothing to do, and the guest is back');
  compose(['up', '-d', '--wait']);
  check(/No migrations were applied|already up to date/i.test(compose(['logs', 'migrate'])), 'the restored schema must be current');
  const me = await retry(() => send('/api/me', { headers: { cookie } }).then((r) => (check(r.status === 200, `GET /api/me returned ${r.status}`), r)));
  const after = JSON.parse(me.body);
  check(after.playerId === player.playerId && after.displayName === 'Backup Chog', 'the restored player must be the original one');
  console.log('Backup and restore smoke test passed.');
}

try {
  await main();
} catch (error) {
  console.error(`Backup and restore smoke test failed: ${error.message}`);
  if (error.stderr) console.error(String(error.stderr));
  try {
    console.error(compose(['logs', '--no-color', '--tail', '60']));
  } catch {
    // The stack may not have started at all.
  }
  process.exitCode = 1;
} finally {
  for (const project of Object.values(projects)) {
    try {
      compose(['down', '-v', '--remove-orphans'], { project });
    } catch {
      // Already removed.
    }
  }
  try {
    run('docker', ['volume', 'rm', '-f', offsiteVolume]);
  } catch {
    // Never created.
  }
  rmSync(workDir, { recursive: true, force: true });
}
