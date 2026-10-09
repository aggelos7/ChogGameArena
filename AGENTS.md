# Agent instructions — Chog Game Arena

Read `docs/requirements.md`, `docs/architecture.md` and `docs/backlog.md` before changing code. Implement exactly **one** backlog story per task unless explicitly instructed otherwise.

1. State the story ID, dependencies, plan, files and tests before edits.
2. Follow the modular monolith: one shared multiplayer engine; no duplicate wallet, NFT, room, realtime or persistence services per game.
3. Server is authoritative. Never trust client dice, cards, rewards, NFT ownership, wallet address claims, ability uses, timers or final scores.
4. MVP ability strategy is `SharedAbilityPool` for all players, including guests. Keep trait normalization and assignment abstraction but **do not** implement `TraitBased` until requested.
5. Players owning many NFTs select **one** verified NFT per match; nonholders use default Chogs. Never stack NFT abilities.
6. Use the Nx workspace (`apps/web`, `libs/*`) for Angular and frontend task orchestration; use native `dotnet` solution commands for the backend under `backend/`. Pin Node.js LTS, Nx and Angular versions. Do not assume an unverified .NET Nx plugin. Prefer simple .NET 8 / Angular / EF Core / PostgreSQL / SignalR / Docker Compose patterns. Do not add Kubernetes, microservices, cloud dependencies or separate game servers.
7. Keep secrets in environment variables, typed config, and ignored files. Never hardcode contract addresses, RPC secrets or wallet private keys.
8. For game logic use deterministic tests with injected random source; production RNG must be server-side and secure.
9. Implement action IDs, expected state versions, room authorization and idempotency before exposing multiplayer commands.
10. Run Nx build/lint/tests for touched frontend projects and native `dotnet build/test` for backend changes. Nx Cloud is optional and off by default. Tests and builds must pass; if not, report exact failures. Do not claim success without running checks.
11. Do not change unrelated files, introduce speculative features, or silently alter documented rules.
12. After completion, report changes, tests, unresolved questions and update story status only if acceptance criteria are satisfied.

## Monorepo execution rules

- Read `nx.json`, `package.json`, `backend/ChogGames.sln` and affected project configuration before adding tools or dependencies.
- Preserve the existing docs; do not regenerate the workspace over them or start implementing unrelated stories.
- Use `npm ci` for reproducible installs; keep the lockfile committed.
- Nx manages development/CI builds, not production runtime. Keep Docker Compose and single-VM topology unchanged.
- Do not enable Nx Cloud, Kubernetes or paid infrastructure without explicit approval.

## Definition of done

- All story acceptance criteria pass.
- Tests and builds pass for changed projects.
- Migration/config/API changes are documented.
- No sensitive values committed.
- No new unapproved dependencies or architectural deviations.

## Suggested task prompt

Implement STORY-001 from `docs/backlog.md`. First read `AGENTS.md`, `docs/requirements.md` and `docs/architecture.md`. List assumptions and dependencies, then implement only this story. Add tests, run checks, and report results. Do not begin the next story.
