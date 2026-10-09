# CHOG GAME ARENA
## Consolidated Product & Technical Requirements — v1.2

**Status:** Implementation baseline  
**Implementation strategy:** Minimal suite foundation → Chog Life → Chog Race → shared progression  
**NFT decision:** Shared ability pool for MVP; trait-based assignment extensibility reserved for future  
**Deployment strategy:** Local-first, staging when external testers are needed, production after validation  
**Workspace tooling:** Nx for Angular and repository task orchestration; native .NET SDK for backend builds

## 1. Product vision and scope
Chog Game Arena is a mobile-first browser-based multiplayer platform for the Chog community on Monad. The first two games are **Chog Life** (portfolio-building life-path board game) and **Chog Race** (competitive board race with action cards). The platform must allow future games and, eventually, alternative community themes without duplicating infrastructure. Game actions are off-chain; wallet ownership checks use Monad RPC. NFT ownership is optional for playing.

**MVP goals:** 2–6 players per room, private room codes and invite links, real-time turns, mobile usability, reconnect, server-authoritative actions, NFT character selection, and one balanced selectable ability per player per match. Target play time: Chog Life 15–20 minutes; Chog Race 10–15 minutes, hard limit 20 minutes for both.

**Out of scope for MVP:** on-chain game moves/rewards, tournaments, friends/chat, marketplace, seasons, paid power-ups, multi-wallet aggregation, Kubernetes, microservices, advanced analytics, and cross-game progression.

## 2. Delivery sequence
1. **Foundation:** Nx monorepo, Angular frontend, ASP.NET Core backend, PostgreSQL, Redis-ready abstraction, Docker Compose, configuration, health checks and automated tests.
2. **Suite skeleton:** landing page, game catalogue/status, guest identity, profile, navigation.
3. **Wallet and NFTs:** wallet authentication, mock NFT provider, real Monad provider, collection browser, selected character, normalized metadata.
4. **Shared multiplayer:** rooms, lobby, ready status, match lifecycle, SignalR, command validation, reconnect, AFK handling; verify with a minimal test game.
5. **Chog Life:** full playable rules, board, events, portfolio, paths, selectable abilities, results.
6. **External testing:** staging deployment, mobile testing, multiplayer/load/security validation; release Chog Life.
7. **Chog Race:** reuse platform, implement race-specific board, deck/cards, attacks, defenses, abilities, victory; release.
8. **Post-MVP:** achievements, XP, leaderboards, seasons, collector cosmetics, additional games/themes.

## 3. Architecture and ownership boundaries
Use a modular monolith, one backend deployment, one frontend, shared PostgreSQL, optional Redis for cache/scale-out. Shared platform owns identity, wallets, NFTs, rooms, sessions, turn orchestration, real-time messaging, command processing, persistence and results. Each game owns rules, board, cards/events, economy, ability implementations, scoring, UI and configuration. The engine must not know NFT traits.

Suggested Nx repository layout (paths are conventions, not deployed service boundaries):
```text
chog-games/
  apps/
    web/                    # Angular SPA (Nx @nx/angular)
  libs/
    ui/                     # Reusable Angular UI
    game-client/            # Shared browser-side game client logic
    shared-types/           # TypeScript-only API/client contracts
  backend/
    ChogGames.sln           # Native .NET solution
    src/
      ChogGames.Api/        # REST, SignalR hubs, composition root
      ChogGames.Core/       # players, rooms, matches, engine, ability contracts
      ChogGames.Games.ChogLife/
      ChogGames.Games.ChogRace/
      ChogGames.Infrastructure/
    tests/
  infrastructure/          # Docker Compose, Dockerfiles, proxy, deploy examples
  docs/                    # requirements, backlog, architecture, decisions
  .github/workflows/       # CI (added in STORY-007)
  AGENTS.md
  nx.json
  package.json
  package-lock.json
```
Nx manages Angular projects and orchestrates repository tasks. The .NET solution remains buildable/testable with `dotnet` directly; Nx may invoke those commands via explicit targets (`nx:run-commands`) rather than depending on a particular .NET Nx plugin. Nx and Node.js are development/CI tooling, **not runtime dependencies** of the production ASP.NET Core container. Nx Cloud is optional and disabled for the MVP; local caching is sufficient. Lock Node.js to a supported LTS version and pin Nx/plugin versions together in the lockfile.

Use stable `gameType` identifiers (`chog-life`, `chog-race`), versioned game configurations, and game-specific state schemas. Do not copy room, SignalR or wallet logic into game modules.

## 4. Suite requirements
- **SUITE-001 — Catalogue:** public mobile-friendly page lists games with `Available`, `Coming Soon`, or `Maintenance`; clicking an available game enters its lobby flow.
- **SUITE-002 — Guests:** users can play without a wallet using a guest identity and default Chog avatar; guest session recovery must be supported on the same browser.
- **SUITE-003 — Wallet sign-in:** wallet connect alone is not authentication. Backend issues a one-time challenge, verifies a wallet signature, binds wallet to player identity, and issues an authenticated session; challenge expires and cannot be replayed.
- **SUITE-004 — Profile:** display name, selected avatar, wallet if connected, games played and wins; future stats/XP fields may be added without changing match logic.
- **SUITE-005 — Navigation:** one suite shell and consistent room, profile, character selection and results experiences across games.
- **SUITE-006 — Extensibility:** adding a game requires registration, rules module, UI module and configuration; no new infrastructure stack.

## 5. NFT collection and ownership
- **NFT-001 — Official collection:** collection contract address and chain ID are configured, not user-supplied; verify actual contract details before live launch.
- **NFT-002 — Discovery:** retrieve all supported Chog NFTs owned by the connected wallet, including token IDs, images and metadata.
- **NFT-003 — Cache:** persist normalized NFT metadata and last refresh time; tolerate RPC outages with bounded cache use, but do not treat stale ownership cache as authoritative for match admission.
- **NFT-004 — Collection UI:** show all owned NFTs, image, token ID and traits; support selecting any eligible NFT.
- **NFT-005 — Guest characters:** provide default characters with equivalent competitive ability access.
- **NFT-006 — Multiple ownership:** owning several NFTs allows character choice, not stacked abilities; exactly one character is selected per match.
- **NFT-007 — Per-game selection:** players may choose a different NFT in each match or each game; selection locks at match start.
- **NFT-008 — Ownership verification:** verify selected NFT ownership server-side before match start. Do not trust the browser's token list.
- **NFT-009 — Transfer during match:** snapshot selected NFT and ability at start; an ownership transfer does not disrupt the current match; revalidate before a new match.
- **NFT-010 — Normalized traits:** store `traitType` and `traitValue` regardless of whether traits currently affect gameplay.
- **NFT-011 — Collector rewards:** reserve future cosmetic collection milestones, badges and victory effects; no MVP gameplay advantages for larger collections.

## 6. Ability system — approved MVP decision
**MVP uses Option 2: every player, NFT owner or guest, sees the same ability pool for the selected game and chooses exactly one ability before marking ready.** The selected ability is locked and normally usable once per match. NFT rarity and traits have no effect on eligibility, strength or use count in MVP.

- **NFT-012 — Strategy abstraction:** define `IAbilityAssignmentStrategy` to resolve eligible ability IDs. Implement `SharedAbilityPoolStrategy` now; reserve `TraitBasedAbilityStrategy` for later.
- **NFT-013 — Selection:** require exactly one valid game-specific ability before Ready; persist selection in lobby and match snapshot.
- **NFT-014 — Execution:** game-specific ability implementations validate state, target, phase, remaining uses and effect; all effects execute on server.
- **NFT-015 — Snapshot:** store ability ID, configuration version, parameters, max uses and used count at match start; later configuration edits must not change active matches.
- **NFT-016 — No stacking:** only the selected ability may be used; other NFTs and traits do not contribute additional effects.
- **NFT-017 — Future traits:** future resolver may map normalized traits to eligible abilities, choose deterministic fallback, and allow one selected ability; do not implement trait assignment in MVP.

**Chog Life MVP ability pool:**

| ID | Name | Effect (once/match) |
|---|---|---|
| `reward_boost` | Reward Boost | Increase one eligible positive reward by 25%. |
| `loss_protection` | Loss Protection | Reduce one eligible negative event loss by 50%. |
| `event_preview` | Event Preview | Reveal the upcoming event before an eligible decision. |
| `event_reroll` | Lucky Reroll | Reroll one eligible random event. |

**Chog Race MVP ability pool:**

| ID | Name | Effect (once/match) |
|---|---|---|
| `movement_boost` | Speed Boost | Add 2 movement to an eligible movement action. |
| `attack_block` | Shield | Block one eligible incoming attack. |
| `card_preview` | Card Vision | Privately view the next card in the deck. |
| `card_steal` | Card Steal | Take one server-random card from an eligible opponent. |

Ability values, activation windows, and per-match limits are configuration-driven. Preview information is private to the player; other players receive only appropriate public state changes.

Suggested contracts:
```csharp
public interface IAbilityAssignmentStrategy
{
    IReadOnlyCollection<string> GetAvailableAbilities(
        GameDefinition game, PlayerCharacter character);
}

public interface IGameAbility
{
    string Id { get; }
    bool CanExecute(GameState state, Player player, AbilityCommand command);
    void Execute(GameState state, Player player, AbilityCommand command);
}
```

## 7. Shared multiplayer engine
- **ENG-001 — Rooms:** create private room with unique invite code/link; 2–6 players; host, membership and capacity validated server-side.
- **ENG-002 — Lobby:** show players, character, selected ability, ready status and host; only valid lobby starts.
- **ENG-003 — Authority:** client submits intents (`ROLL`, `SELECT_PATH`, `PLAY_CARD`, `USE_ABILITY`, `END_TURN`); server decides outcomes and emits events.
- **ENG-004 — State:** server maintains authoritative versioned match state, current player, phase, turn number, random outcomes, ability usage and results.
- **ENG-005 — Realtime:** SignalR delivers lobby/match updates; reconnect fetches complete current snapshot, not just missed messages.
- **ENG-006 — Reconnect:** authenticate player, rejoin appropriate room, restore state after browser refresh/network loss; reject takeover by unrelated user.
- **ENG-007 — AFK:** configurable turn timer (initial 20 seconds); perform game-specific safe default or skip. Resolve mandatory reactions/choices deterministically.
- **ENG-008 — Randomness:** all dice, cards, risk outcomes and targets generated server-side; use cryptographically secure randomness for unpredictable outcomes.
- **ENG-009 — Idempotency:** each command has `actionId`, `matchId` and expected state version; deduplicate retries, reject stale commands, ensure transactional state changes.
- **ENG-010 — Lifecycle:** waiting → active → completed/abandoned; store match start/end and final standings.
- **ENG-011 — Time limit:** enforce 20-minute maximum match duration, with game-specific deterministic timeout scoring/tiebreakers.
- **ENG-012 — Rules plugins:** common engine coordinates turns and events; individual game modules own resolution logic.
- **ENG-013 — Privacy:** hidden hands, upcoming cards and private event previews must never leak to opponents in shared snapshots.
- **ENG-014 — Resilience:** persist checkpoints/important transitions so server restarts do not silently corrupt active matches; document recovery behavior.

Example command:
```json
{"actionId":"uuid","matchId":"uuid","expectedVersion":14,"type":"ROLL"}
```

## 8. Chog Life requirements
**Concept:** 2–6 Chogs move through a Web3 life journey. Players balance safe investments and degen risk. Highest final portfolio wins; reaching the finish first does not guarantee victory.

- **LIFE-001 — Board:** configurable 30–40 spaces with normal, reward, event, risk, safe/degen branch, NFT, market and special tiles.
- **LIFE-002 — Start:** each player begins at Start with 100 fictional MON (game points, not real tokens), zero liabilities/assets, and one selected ability.
- **LIFE-003 — Turn:** roll → move → resolve tile → optional path/choice → resolve event → end turn; server validates sequence.
- **LIFE-004 — Events:** configurable Airdrop, Rug Pull, Pump, Liquidation, Whale, Market Crash, NFT Mint, FOMO and similar Web3 events.
- **LIFE-005 — Safe/Degen:** at branch tiles choose a lower-variance safe route or a high-risk degen route. Example safe: invest 100, gain 25; degen: invest 100, chance of 3x or 80% loss. Probabilities must be explicit in config and tested for balance.
- **LIFE-006 — Economy:** track liquid MON, investment/asset values, bonuses and liabilities; `portfolio = liquid + assets + bonuses - liabilities`.
- **LIFE-007 — Finish:** optional first-finish bonus (initial suggestion +100 MON); determine winner by final portfolio after all players finish or hard timeout. Define deterministic tie-breaker.
- **LIFE-008 — Abilities:** implement the four Chog Life abilities in Section 6, with clear activation windows and server validation.
- **LIFE-009 — Duration:** tune movement, board length, timers and event frequency for 15–20 minutes.
- **LIFE-010 — Results:** rank, starting/final portfolio, airdrops, rugs survived, liquidations, biggest win, Degen Score; shareable result image may follow MVP.
- **LIFE-011 — Economy safety:** negative balances, insufficient funds, overlapping multipliers and simultaneous global market events have deterministic rules.

**Example event configuration:**
```json
{"id":"rug","category":"risk","lossPercent":40,"minimumLoss":0}
```

## 9. Chog Race requirements
**Concept:** 2–6 Chogs compete to reach the final tile, using movement, action cards, attacks and defenses.

- **RACE-001 — Board:** configurable 24–30 spaces, including optional special tiles.
- **RACE-002 — Movement:** server rolls 1–3 spaces by default; card/ability modifiers may apply.
- **RACE-003 — Hand:** configurable max 3 cards per player; maintain hidden hand state and server-controlled deck/discard pile.
- **RACE-004 — Turn:** movement → tile resolution/card draw → optional card play → reactions if applicable → end turn.
- **RACE-005 — Cards:** implement initial examples: Pump (+3), Rug (opponent −4), HODL (block attack), Whale (swap positions), Airdrop (additional movement action), Liquidated (skip next turn), FOMO (+5 or −3), Reverse (opponents −1). All values configurable.
- **RACE-006 — Reactions:** define explicit defense windows, timeouts and attack-resolution order; protect against duplicate responses.
- **RACE-007 — Abilities:** implement four Chog Race abilities from Section 6; validate hand capacity, targets and privacy.
- **RACE-008 — Victory:** first player to reach or pass final tile wins, subject to a documented simultaneous-effect resolution rule.
- **RACE-009 — Duration:** target 10–15 minutes; hard timeout at 20 minutes, winner determined by furthest position then deterministic tie-breakers.
- **RACE-010 — Future modes:** best-of-three, teams and tournaments are out of MVP.

## 10. Data and APIs
**Initial entities:** Player, Wallet, ChogNft, NftTrait, GameDefinition, GameRoom, RoomPlayer, GameSession, MatchPlayer, MatchResult, AbilityDefinition, AbilitySelection, GameConfigVersion. Future: Achievement, PlayerAchievement, Season, Leaderboard, XPTransaction.

**REST endpoints (illustrative):** `GET /api/games`, `GET /api/profile`, `POST /api/wallet/challenge`, `POST /api/wallet/verify`, `GET /api/nfts`, `POST /api/rooms`, `POST /api/rooms/{id}/join`, `GET /api/matches/{id}`, `GET /health`.

**SignalR:** authenticated room subscription; lobby events, match commands and per-player private updates. Never accept caller-supplied player IDs as proof of identity.

Persist match results and game-config version; avoid storing full raw event history until needed. Apply EF Core migrations under source control.

## 11. Infrastructure and environments
**Local (no paid hosting required):** developer machine with Node.js LTS, Nx, Angular, .NET SDK/API, PostgreSQL and Redis via Docker Compose (or frontend/backend running directly). Mock NFT provider must support offline gameplay; real Monad RPC is optional for integration testing. Use `.env.example` with placeholders, not real secrets.

**Staging (when external testing starts):** one Linux VM, initial planning size 2 vCPU / 4 GB RAM / 40+ GB SSD; Docker Compose with API, PostgreSQL, Redis, reverse proxy; frontend on Cloudflare Pages or equivalent static host. HTTPS and WebSockets required.

**Production initial topology:** same single-VM design, frontend on CDN, backend/SignalR/PostgreSQL/Redis on VM, managed Monad RPC provider. Initial planning size 2 vCPU / 4 GB RAM / 40–80 GB SSD, subject to load tests and disk/backup requirements. Both games share infrastructure; cost is driven by concurrent users, not game count.

**Production readiness:** persistent volumes, automated database backups, off-VM backup copy, restore test, TLS, secret management, container health checks, structured logs, basic metrics/alerts, security updates and rollback procedure. Staging and production must not share a database.

**Future scale:** vertical resize first; then move database to managed/dedicated hosting, add load balancer and multiple API instances with Redis-backed SignalR scale-out/distributed state coordination. Do not assume a given concurrent-player capacity without representative load testing.

## 12. Security, reliability and testing
- Authenticate wallet signatures with expiring one-time nonces; secure sessions and CORS; HTTPS outside localhost.
- Validate all actions, room membership, phases, abilities, targets, inventory and balances server-side.
- Apply rate limiting to wallet, room and action endpoints; avoid client-authoritative random values.
- Handle RPC failure, metadata errors, malformed images and unknown traits gracefully.
- Enforce transactional command processing and idempotency; ensure private data is not broadcast.
- Unit-test deterministic game rules with seeded/test-controlled RNG; integration-test EF Core, SignalR and reconnect flows.
- Simulate disconnects, duplicate commands, stale versions, AFK, simultaneous reactions, server restart and full-room joins.
- Run representative multiplayer load tests before public launch; measure CPU, RAM, latency, connection count and database load.
- Add GitHub Actions CI checks for Nx-managed Angular build/lint/tests, native .NET build/tests, migrations, Compose validation and container image generation. Use Nx affected tasks only when the CI git base/head history is available; backend checks remain mandatory until affected-project integration is explicitly verified.

## 13. Acceptance criteria samples
**Room creation:** Given a guest or authenticated player, when they create a Chog Life room, then a unique code is generated, they become host, room status is Waiting, and capacity is six.

**Multiple NFTs:** Given a verified wallet with several eligible Chogs, when the player opens My Chogs, then all owned NFTs appear and exactly one may be selected for the upcoming match.

**Equal abilities:** Given an NFT owner and a guest in the same Chog Life lobby, when they choose abilities, then both receive the same four available options regardless of NFT traits.

**Ability lock:** Given a player selected Reward Boost and marked Ready, when the match starts, then the chosen ability and versioned parameters are snapshotted and cannot be changed mid-match.

**Reconnection:** Given a participant disconnects mid-match, when the same authenticated/guest identity reconnects, then the latest authorized state is restored without duplicate actions.

**No trait coupling:** Given a new trait is added to NFT metadata, when a match uses SharedAbilityPool, then ability availability is unchanged and no game engine code requires modification.

## 14. AI-assisted implementation contract
For each coding-agent task provide: requirement IDs, goal, target files/modules, input/output contracts, persistence changes, validations, acceptance tests, exclusions and a verification command. Keep tasks narrowly scoped. Require the agent to inspect existing code before edits, avoid inventing rules, add tests, run build/tests, and report changed files plus unresolved assumptions.

**Example task:**
```text
Implement ENG-001: private room creation.
Stack: ASP.NET Core, EF Core/PostgreSQL.
Create GameRoom with Id, Code, HostPlayerId, GameType, Status, CreatedAt.
Room code must be unique; max capacity 6; game type must be registered.
Expose POST /api/rooms and tests for success, invalid game type and code collision.
Do not implement SignalR, matchmaking or game turns in this task.
Acceptance: API returns room ID/code and persists Waiting room with correct host.
```

## 15. Decisions to finalize before gameplay coding
These values are currently **proposed defaults**, not fully playtested final rules: exact Chog Life board topology, dice distribution, safe/degen odds, event deck and portfolio valuations, finish behavior at time limit, Race draw cadence, card play limits/reaction timing, ability activation windows and tie-breakers. Finalize each game's rules as versioned configuration and deterministic tests before building polished visuals. Confirm official Chog NFT contract, metadata schema and wallet-signing compatibility before implementing live NFT integration.

**Core principle:** build one small suite foundation locally, release one complete game at a time, and preserve the clean boundary between NFT metadata, ability assignment, game rules and shared multiplayer infrastructure.
