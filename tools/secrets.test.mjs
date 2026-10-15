// STORY-005 secret hygiene: no credential-like values in files that git tracks (or would
// track). Placeholders and test-only values in test code are allowed.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { basename, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const files = execFileSync('git', ['ls-files', '-co', '--exclude-standard'], { cwd: root, encoding: 'utf8' })
  .split('\n')
  .filter(Boolean)
  .filter((file) => !file.endsWith('package-lock.json'));
const read = (file) => {
  try {
    return readFileSync(join(root, file), 'utf8');
  } catch {
    return ''; // deleted in the working tree
  }
};
const isTestFile = (file) => /(^|\/)tests?\//.test(file) || /\.(test|spec)\.[cm]?[jt]s$/.test(file);
// 'ci-only-password' secures only the throwaway PostgreSQL service container in CI.
const PLACEHOLDER_PASSWORDS = new Set(['change-me-local-only', 'ci-only-password', '<password>', '<value>']);

test('local secret files are never tracked', () => {
  const forbidden = files.filter((file) => {
    const name = basename(file);
    return (name.startsWith('.env') && name !== '.env.example') || name === 'secrets.json' || /\.(pfx|p12|pem|key)$/.test(name);
  });
  assert.deepEqual(forbidden, []);
});

test('appsettings files contain no connection strings or credentials', () => {
  for (const file of files.filter((f) => /appsettings[^/]*\.json$/.test(f))) {
    assert.doesNotMatch(read(file), /ConnectionString|Password|Secret|ApiKey|PrivateKey/i, file);
  }
});

test('connection-string passwords outside tests are placeholders', () => {
  const offenders = [];
  for (const file of files.filter((f) => !isTestFile(f))) {
    for (const [, value] of read(file).matchAll(/Password=([^;"'\s]+)/g)) {
      if (!PLACEHOLDER_PASSWORDS.has(value) && !value.startsWith('$')) offenders.push(`${file}: Password=${value}`);
    }
  }
  assert.deepEqual(offenders, []);
});

test('no private keys or access tokens are committed', () => {
  const patterns = [
    /-----BEGIN [A-Z ]*PRIVATE KEY-----/,
    /\b(gh[pousr]_[A-Za-z0-9]{36,})\b/, // GitHub tokens
    /\bAKIA[0-9A-Z]{16}\b/, // AWS access key id
    /\b0x[a-fA-F0-9]{64}\b/, // raw 32-byte hex, e.g. a wallet private key
    /AGE-SECRET-KEY-1[0-9A-Z]{58}/, // an age private key (backup decryption, STORY-047)
  ];
  const offenders = files.flatMap((file) => patterns.filter((p) => p.test(read(file))).map((p) => `${file} matches ${p}`));
  assert.deepEqual(offenders, []);
});
