# Chog Game Arena — Architecture v1.0

## Decisions

- **ADR-001:** Modular monolith: one ASP.NET Core backend, distinct Core/Infrastructure/Games modules, one Angular SPA. No per-game server for MVP.
- **ADR-002:** Local-first Docker Compose for PostgreSQL and Redis; backend/frontend may run in containers or local IDE. Staging only for external testing.
- **ADR-003:** Backend authoritative for game rules, randomness, state transitions and result calculation. Clients send commands, never computed outcomes.
- **ADR-004:** One reusable room, matchmaking-ready lobby, turn scheduler, reconnect and SignalR layer. Game modules implement their own rules.
- **ADR-005:** Wallet sign-in requires server nonce/signature verification; public address alone is not authentication. Guest session uses opaque server identity.
- **ADR-006:** All owned official Chog NFTs are visible; one selected per match. Server verifies ownership at start and snapshots character/ability.
- **ADR-007:** MVP uses `SharedAbilityPool` for NFT holders and guests alike. `IAbilityAssignmentStrategy` permits future `TraitBased` implementation without changing the game engine.
- **ADR-008:** Match configuration and ability versions are snapshotted at start. Existing matches do not change when rules are updated.
- **ADR-009:** No real MON transfers, staking, gambling or token payout in MVP. MON is an explicitly fictional in-game score currency.
- **ADR-010:** PostgreSQL is durable system of record. Redis is optional cache/coordination and future SignalR backplane; do not rely solely on Redis for completed results.

## System context

```text
Browser (Angular SPA)
  |-- REST (profile, NFT collection, rooms, history)
  |-- SignalR (lobby, commands, authoritative state)
  v
ASP.NET Core modular monolith
  |-- Identity and Wallet Auth
  |-- NFT Provider / Metadata Cache
  |-- Room and Match Orchestration
  |-- Game Engine + Ability Resolver
  |      |-- Chog Life Rules
  |      `-- Chog Race Rules
  |-- PostgreSQL (EF Core)
  |-- Redis (cache / future coordination)
  `-- Monad RPC / NFT indexer adapter
```

## Backend module boundaries

- `Core`: domain models, command envelopes, contracts, game engine abstractions, ability assignment interfaces. No infrastructure dependencies.
- `Games.ChogLife`: board, events, economy, decisions, scoring and ability handlers.
- `Games.ChogRace`: board, cards, reactions, finish conditions and ability handlers.
- `Infrastructure`: EF Core, Redis, blockchain RPC/indexer, caching, hosted background tasks.
- `Api`: REST endpoints, SignalR hubs, auth middleware and DI composition.

## Core contracts (conceptual, not prescribed signatures)

- `IGameRules`: initialize, validate command, apply action, check completion.
- `IAbilityAssignmentStrategy`: resolve eligible ability IDs from game definition and character snapshot.
- `IAbilityHandler`: validate timing/target and apply a deterministic effect.
- `INftProvider`: retrieve and verify ownership and normalized metadata.
- `IRandomSource`: secure production randomness; seeded deterministic source in tests.
- `IMatchRepository`: persist versioned authoritative state and results.

## State and concurrency

- Room lifecycle: Waiting -> Active -> Completed; cancellation allowed only by explicit policy.
- Match commands carry `actionId`, `matchId`, `expectedVersion`, `type` and payload. Authenticated player identity comes from the server session, never client payload.
- Process commands serially per match or use optimistic concurrency on a match version; duplicates must be idempotent.
- Publish state changes after durable commit. On reconnect send current snapshot and version.
- Use a single in-process authoritative scheduler initially; persist turn deadlines and document recovery behavior on restart.
- Preserve hidden card/deck information in server state; send filtered player-specific views.

## Persistence (initial conceptual entities)

Player, GuestSession, WalletChallenge, WalletLink, ChogNft, NftTrait, GameDefinition, GameRoom, RoomMember, GameSession, MatchPlayer, MatchSnapshot, ProcessedAction, MatchResult, PlayerStats.

Use stable IDs, indexes on room code, match status and wallet/token identity. Migrations must be reviewed before deploy.

## Nx monorepo and build boundaries

- **Workspace:** Nx at repository root (`nx.json`, root `package.json`, lockfile). Use `@nx/angular` for `apps/web` and browser-side libraries under `libs/`.
- **Backend:** Keep `backend/ChogGames.sln` and .NET projects under `backend/src` and `backend/tests`. Native `dotnet restore/build/test` remains the source of truth; optionally expose Nx targets via `nx:run-commands` in a root `project.json` or explicit Nx project configuration.
- **Nx integration:** Do not assume an `@nx/dotnet` package exists or is compatible without checking the installed Nx release. For MVP, prefer native .NET commands wrapped by Nx targets over unverified plugins.
- **Caching:** Local Nx cache for deterministic frontend tasks; backend tasks must not be cached until inputs/outputs and correctness are verified. Nx Cloud is not required.
- **Dependency direction:** Angular libs depend inward on shared contracts; browser code never imports backend C# code. Share API contracts through OpenAPI-generated TypeScript clients or explicitly maintained schemas, not runtime coupling.
- **CI:** GitHub Actions installs pinned Node.js and .NET SDK versions, uses `npm ci`, runs frontend Nx targets, then runs `dotnet` restore/build/test. Nx affected optimizations may be added after baseline full checks work.
- **Deployment:** Docker builds compile frontend and backend in build stages; production runs Nginx/static Angular assets and .NET runtime containers. Nx/Node are not required on the VM host at runtime. PostgreSQL, Redis, SignalR, HTTPS/WSS and Docker Compose topology do not change.

### Repository convention

```text
apps/web/                  Angular app
libs/ui/                   Shared UI
libs/game-client/          Shared game client
libs/shared-types/         TypeScript contracts
backend/ChogGames.sln      Native .NET solution
backend/src/               Backend application and game modules
backend/tests/             Backend tests
infrastructure/            Compose and deployment assets
docs/                      Product and engineering specifications
AGENTS.md                  AI coding agent rules
nx.json                    Nx workspace configuration
package.json               Workspace scripts and dependencies
```

## Environments

- **Local:** Angular dev server, ASP.NET Core API, PostgreSQL and Redis in Docker Compose; mock NFTs by default, optional real Monad provider.
- **Staging:** One Linux VM initially (starting estimate 2 vCPU / 4 GB RAM / 40–80 GB disk) running backend + database + Redis + reverse proxy; static frontend via CDN or proxy. Size requires load testing.
- **Production:** Same container topology initially with HTTPS, backups, monitoring, firewall, secret management, restore procedure and deployment rollback. Do not share staging and production databases.
- **Scale later:** vertical scaling first; multi-instance SignalR requires compatible Redis backplane or managed service and distributed coordination for game commands/timers. Never run two independent authoritative schedulers for one match.

## Security and reliability

- Verify wallet challenge signatures server-side, one-time nonces, domain and expiry; validate official NFT contract and chain.
- Authorize all room, match and ability commands; rate-limit APIs; use HTTPS/WSS in staging and production.
- Keep RPC keys, signing secrets and database credentials out of git and client bundles.
- Encrypt backups, test restore, record health/readiness and correlate command logs without leaking private data.
- Use server time for deadlines and authoritative secure RNG; use injected deterministic RNG in tests.

## Open decisions before implementing dependent stories

1. Verify official Chog collection contract address, metadata standard, chain ID and reliable ownership/indexer API.
2. Confirm official NFT artwork usage rights and default nonholder character assets.
3. Finalize Chog Life turn cadence, dice range, exact finish trigger and tie-break rules in a versioned rules config.
4. Finalize Race reaction windows, draw timing and how effects resolve at the finish tile.
5. Define guest account persistence and whether wallet linking merges existing guest match history.

Record outcomes in `docs/decisions.md` before dependent features are coded; do not invent blockchain addresses or token mechanics.
