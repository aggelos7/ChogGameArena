// STORY-007 guards for the CI workflow: full checks on every PR, backend never skipped,
// no Nx Cloud, pinned actions and toolchains taken from the repository's version files.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const workflow = readFileSync(join(root, '.github', 'workflows', 'ci.yml'), 'utf8');

test('runs on pull requests and pushes to develop and master', () => {
  assert.match(workflow, /pull_request:\s*\n\s*branches: \[develop, master\]/);
  assert.match(workflow, /push:\s*\n\s*branches: \[develop, master\]/);
});

test('never filters by path or uses Nx affected, so backend checks always run', () => {
  assert.doesNotMatch(workflow, /paths(-ignore)?:/);
  assert.doesNotMatch(workflow, /\baffected\b/);
  assert.doesNotMatch(workflow, /^\s*if:(?!\s*always\(\))/m, 'only always() conditions are allowed');
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
