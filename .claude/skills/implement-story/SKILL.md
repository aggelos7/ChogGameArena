---
name: implement-story
description: Implement exactly one Chog Game Arena backlog story (e.g. STORY-002) in a new feature branch created from develop. Use when asked to start, implement or work on a STORY-xxx.
---

# Implement a backlog story

Argument: a story ID such as `STORY-002`. If none is given, pick the first unblocked `Todo` story in `docs/backlog.md`.

## Branching rule

Every story is implemented in its **own new feature branch created from `develop`**. Never commit to `develop` or `master` directly.

```bash
git fetch origin
git checkout develop && git pull --ff-only origin develop
git checkout -b feature/story-<nnn>-<short-kebab-title>   # e.g. feature/story-002-backend-solution
```

- The working tree must be clean before branching; if it is not, stop and report why.
- One story per branch and per PR. Do not start the next story.
- Open the PR **into `develop`** with `Closes #<issue>`; the issue number is found with `gh issue list --search "STORY-<nnn>"`.

## Steps

1. Read `AGENTS.md`, `docs/requirements.md`, `docs/architecture.md`, `docs/decisions.md` and the story in `docs/backlog.md`. Check that earlier dependent stories are `Done` and open decisions in `docs/decisions.md` do not block it.
2. Before editing, state: story ID, dependencies, assumptions, plan, files to change and tests.
3. Create the feature branch from `develop` (above).
4. Implement only the story's scope and honour its scope guard. Server-authoritative, shared engine, no secrets, no unapproved dependencies (see `AGENTS.md`).
5. Add tests. Run native `dotnet` build/test and the relevant Nx targets for every changed project; report the exact results, including failures.
6. Update docs, migrations, config and API contract if changed.
7. Set the story `Status` to `Done` in `docs/backlog.md` only when every acceptance criterion is verified.
8. Commit on the feature branch with a message starting `STORY-<nnn>:`. Push and open a PR into `develop` using `.github/pull_request_template.md`. Commit, push and PR creation only when the user has asked for them.
9. Report: changes, test results, unresolved questions. Stop; do not begin the next story.
