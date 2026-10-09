#!/usr/bin/env node
// Syncs docs/backlog.md to GitHub: labels, epic milestones, one issue per story and a
// Projects board. Idempotent (existing labels/milestones/issues are skipped).
// Dry run by default; pass --apply to change GitHub. Requires `gh` logged in with
// the `repo` and `project` scopes.
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const APPLY = process.argv.includes('--apply');
const PROJECT_TITLE = 'Chog Game Arena';
const backlogPath = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'docs', 'backlog.md');

const gh = (args, input) =>
  execFileSync('gh', args, { encoding: 'utf8', input, stdio: ['pipe', 'pipe', 'pipe'] }).trim();
const ghJson = (args) => JSON.parse(gh(args));

export function parseBacklog(text) {
  const epics = new Map();
  for (const m of text.matchAll(/^## (E\d+) — (.+)$/gm)) epics.set(m[1], m[2].trim());

  const stories = [];
  const parts = text.split(/^#### (STORY-\d+) — (.+)$/m);
  for (let i = 1; i < parts.length; i += 3) {
    const block = parts[i + 2].split(/^#{1,3} /m)[0]; // stop at next feature/epic heading
    const field = (name) => block.match(new RegExp(`^- \\*\\*${name}:\\*\\* (.+)$`, 'm'))?.[1].trim();
    stories.push({
      id: parts[i],
      title: parts[i + 1].trim(),
      status: field('Status'),
      priority: field('Priority'),
      epic: field('Epic'),
      body: block.replace(/^- \*\*(Status|Priority|Epic):\*\*.*\n/gm, '').trim(),
    });
  }
  return { epics, stories };
}

const issueTitle = (s) => `${s.id} — ${s.title}`;
const issueBody = (s, epics) =>
  [
    `**Epic:** ${s.epic} — ${epics.get(s.epic)}  ·  **Priority:** ${s.priority}`,
    '',
    s.body,
    '',
    '---',
    'Source of truth: `docs/backlog.md`. Implement exactly one story per PR (see `AGENTS.md`); ' +
      'follow story IDs in ascending order and start only when earlier stories are unblocked. ' +
      'Reference this issue from the PR with `Closes #<number>`.',
  ].join('\n');

const LABELS = (epics, stories) => [
  { name: 'story', color: '1d76db', description: 'One backlog story' },
  ...[...epics].map(([id, t]) => ({ name: `epic:${id}`, color: '5319e7', description: `${id} — ${t}`.slice(0, 100) })),
  ...[...new Set(stories.map((s) => s.priority))].sort().map((p) => ({
    name: `priority:${p}`,
    color: p === 'P1' ? 'b60205' : p === 'P2' ? 'fbca04' : '0e8a16',
    description: `Backlog priority ${p}`,
  })),
];

const { epics, stories } = parseBacklog(readFileSync(backlogPath, 'utf8'));
const labels = LABELS(epics, stories);
console.log(`${APPLY ? 'APPLY' : 'DRY RUN'}: ${labels.length} labels, ${epics.size} milestones, ${stories.length} issues, 1 project`);

if (!APPLY) {
  for (const s of stories) console.log(`  ${s.status === 'Done' ? '[closed]' : '[open]  '} ${issueTitle(s)}  (${s.epic}, ${s.priority})`);
  console.log('\nRe-run with --apply to publish.');
  process.exit(0);
}

const repo = ghJson(['repo', 'view', '--json', 'nameWithOwner']).nameWithOwner;
const owner = repo.split('/')[0];

const existingLabels = new Set(ghJson(['label', 'list', '--limit', '200', '--json', 'name']).map((l) => l.name));
for (const l of labels) {
  if (existingLabels.has(l.name)) continue;
  gh(['label', 'create', l.name, '--color', l.color, '--description', l.description]);
  console.log(`label ${l.name}`);
}

const msTitle = (id) => `${id} — ${epics.get(id)}`;
const existingMs = new Set(
  ghJson(['api', `repos/${repo}/milestones?state=all&per_page=100`]).map((m) => m.title),
);
for (const id of epics.keys()) {
  if (existingMs.has(msTitle(id))) continue;
  gh(['api', `repos/${repo}/milestones`, '-f', `title=${msTitle(id)}`, '-f', `description=Epic ${id} from docs/backlog.md`]);
  console.log(`milestone ${msTitle(id)}`);
}

let project = ghJson(['project', 'list', '--owner', owner, '--format', 'json']).projects.find((p) => p.title === PROJECT_TITLE);
if (!project) {
  project = ghJson(['project', 'create', '--owner', owner, '--title', PROJECT_TITLE, '--format', 'json']);
  console.log(`project #${project.number}`);
}
try { gh(['project', 'link', String(project.number), '--owner', owner, '--repo', repo]); } catch { /* already linked */ }

const existingIssues = new Map(
  ghJson(['issue', 'list', '--state', 'all', '--limit', '500', '--json', 'title,number']).map((i) => [i.title, i.number]),
);
for (const s of stories) {
  const title = issueTitle(s);
  if (existingIssues.has(title)) { console.log(`skip #${existingIssues.get(title)} ${title}`); continue; }
  const url = gh(
    ['issue', 'create', '--title', title, '--body-file', '-', '--label', `story,epic:${s.epic},priority:${s.priority}`,
      '--milestone', msTitle(s.epic), '--project', PROJECT_TITLE],
    issueBody(s, epics),
  );
  console.log(`issue ${url}`);
  if (s.status === 'Done') gh(['issue', 'close', url, '--reason', 'completed']);
}
console.log(`\nBoard: https://github.com/users/${owner}/projects/${project.number}`);
