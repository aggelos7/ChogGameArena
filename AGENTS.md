# Agent instructions — Chog Games Suite

Read `docs/requirements.md`, `docs/architecture.md` and `docs/backlog.md` before changing code. Implement exactly **one** backlog story per task unless explicitly instructed otherwise.

1. State the story ID, dependencies, plan, files and tests before edits.
2. Follow the modular monolith: one shared multiplayer engine; no duplicate wallet, NFT, room, realtime or persistence services per game.
3. Server is authoritative. Never trust client dice, cards, rewards, NFT ownership, wallet address claims, ability uses, timers or final scores.
4. MVP ability strategy is `SharedAbilityPool` for all players, including guests. Keep trait normalization and assignment abstraction but **do not** implement `TraitBased` until requested.
5. Players owning many NFTs select **one** verified NFT per match; nonholders use default Chogs. Never stack NFT abilities.
6. Prefer simple .NET 8 / Angular / EF Core / PostgreSQL / SignalR / Docker Compose patterns. Do not add Kubernetes, microservices, cloud dependencies or separate game servers.
7. Keep secrets in environment variables, typed config, and ignored files. Never hardcode contract addresses, RPC secrets or wallet private keys.
8. For game logic use deterministic tests with injected random source; production RNG must be server-side and secure.
9. Implement action IDs, expected state versions, room authorization and idempotency before exposing multiplayer commands.
10. Tests and builds must pass; if not, report exact failures. Do not claim success without running checks.
11. Do not change unrelated files, introduce speculative features, or silently alter documented rules.
12. After completion, report changes, tests, unresolved questions and update story status only if acceptance criteria are satisfied.

## Definition of done

- All story acceptance criteria pass.
- Tests and builds pass for changed projects.
- Migration/config/API changes are documented.
- No sensitive values committed.
- No new unapproved dependencies or architectural deviations.

## Suggested task prompt

Implement STORY-001 from `docs/backlog.md`. First read `AGENTS.md`, `docs/requirements.md` and `docs/architecture.md`. List assumptions and dependencies, then implement only this story. Add tests, run checks, and report results. Do not begin the next story.
