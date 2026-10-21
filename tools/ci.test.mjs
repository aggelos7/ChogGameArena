// STORY-007 guards for the CI workflow: full checks on every PR (but Markdown-only changes, DEC-079), backend
// never skipped, no Nx Cloud, pinned actions and toolchains taken from the repository's version files.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const workflow = readFileSync(join(root, '.github', 'workflows', 'ci.yml'), 'utf8');

test('runs on pull requests into develop and master and on pushes to master (DEC-079)', () => {
  assert.match(workflow, /pull_request:\s*\n\s*branches: \[develop, master\]/);
  // A merge into develop was already checked as its pull request.
  assert.match(workflow, /push:\s*\n\s*branches: \[master\]/);
});

test('skips only Markdown-only changes, never code, and never uses Nx affected, so backend checks always run', () => {
  assert.doesNotMatch(workflow, /paths-ignore:/);
  assert.doesNotMatch(workflow, /\baffected\b/);
  assert.doesNotMatch(workflow, /^\s*if:(?!\s*always\(\))/m, 'only always() conditions are allowed');
  const lists = [...workflow.matchAll(/^(\s*)paths:\s*\n((?:\1\s+- .*\n)+)/gm)].map((match) =>
    match[2].split('\n').filter((line) => line.trim()).map((line) => line.trim().replace(/^- "(.*)"$/, '$1')),
  );
  assert.equal(lists.length, 2, 'pull requests and pushes each filter by path');
  // Markdown the workspace checks read, which a change must still be checked for: found in their sources, as
  // 'docs/name.md' or join(root, 'docs', 'name.md').
  const checkedDocs = [
    ...new Set(
      readdirSync(join(root, 'tools'))
        .filter((file) => file.endsWith('.mjs') && file !== 'ci.test.mjs')
        .flatMap((file) =>
          [...readFileSync(join(root, 'tools', file), 'utf8').matchAll(/'((?:docs\/)?[\w.-]+\.md)'|'docs', '([\w.-]+\.md)'/g)].map(
            (match) => match[1] ?? `docs/${match[2]}`,
          ),
        ),
    ),
  ];
  assert.ok(checkedDocs.includes('docs/release-checklist.md'));
  for (const paths of lists) {
    assert.deepEqual(paths.slice(0, 2), ['**', '!**/*.md'], 'everything but Markdown runs the checks');
    assert.deepEqual(paths.filter((path) => path.startsWith('!')), ['!**/*.md'], 'nothing else is skipped');
    assert.deepEqual(paths.slice(2).sort(), [...checkedDocs].sort());
  }
});

test('does not use Nx Cloud', () => {
  assert.match(workflow, /NX_NO_CLOUD: "true"/);
  assert.doesNotMatch(workflow, /NX_CLOUD_ACCESS_TOKEN|nx-cloud/i);
});

test('runs frontend, native .NET and Compose checks', () => {
  for (const step of [
    'npm ci',
    'npm run test:workspace',
    'npx nx run-many --targets=lint,test,build',
    'dotnet restore ChogGames.sln',
    'dotnet build ChogGames.sln',
    'dotnet test ChogGames.sln',
    'dotnet ef migrations has-pending-model-changes',
    'dotnet ef database update',
    'docker compose -f infrastructure/docker-compose.yml config',
    'npm run deps:verify',
    'docker compose -f infrastructure/production/docker-compose.yml config',
    'npm run deploy:verify',
    'npm run backup:verify',
  ]) {
    assert.ok(workflow.includes(step), `missing step: ${step}`);
  }
  assert.match(workflow, /CHOG_TEST_POSTGRES:/, 'PostgreSQL integration tests must run in CI');
});

test('toolchains come from version files and third-party actions are pinned to commit SHAs', () => {
  assert.match(workflow, /node-version-file: \.nvmrc/);
  assert.match(workflow, /global-json-file: backend\/global\.json/);
  const uses = [...workflow.matchAll(/uses: (\S+)/g)].map((m) => m[1]);
  assert.ok(uses.length > 0);
  for (const action of uses) assert.match(action, /@[0-9a-f]{40}$/, `${action} must be pinned to a commit SHA`);
});

test('grants read-only repository permissions', () => {
  assert.match(workflow, /^permissions:\s*\n\s*contents: read\s*$/m);
});
