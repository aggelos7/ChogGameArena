# Progress

Snapshot of where the project stands, for picking work up in a new session. Source of truth for scope is
`docs/backlog.md`; for decisions, `docs/decisions.md`. Last updated 2026-10-13 (STORY-032).

## Stories done

| Story | PR | Summary |
|---|---|---|
| STORY-001 | (initial) | Nx monorepo skeleton with pinned Node 22, Nx and workspace checks. |
| STORY-002 | #63 | Native .NET 8 solution: Api, Core, Games.*, Infrastructure, with architecture tests on project references. |
| STORY-003 | #64 | Angular 22 SPA in `apps/web`: standalone, zoneless, Vitest. |
| STORY-004 | #65 | Docker Compose with pinned PostgreSQL and Redis on localhost, plus a smoke test (`deps:verify`). |
| STORY-005 | #67 | Typed, validated configuration; secrets only in user-secrets, env vars or ignored files; secret scan in workspace checks. |
| STORY-006 | #68 | EF Core migrations applied explicitly (`db:migrate`); `/health/live` and `/health/ready`. |
| STORY-007 | #70 | GitHub Actions CI: frontend, backend with Postgres, model drift and migration checks, Compose smoke test. |
| STORY-008 | #72 | Game catalogue from configuration (`Games:Items`) with Available, Coming Soon and Maintenance statuses. |
| STORY-009 | #73 | Guest identity: a server-side player plus an opaque HttpOnly session cookie (DEC-012, DEC-013). |
| STORY-010 | #75 | Player profile: display name, built-in avatar and stats. |
| STORY-011 | #77 | Mobile-first shell: 375px first, 44px tap targets, navigation tabs. |
| STORY-012 | #79 | Wallet connect through EIP-1193/6963 with no SDK, and a network switch prompt (DEC-014). |
| STORY-013 | #80 | EIP-4361 challenge/sign wallet sign-in that upgrades or switches the player (DEC-015). |
| STORY-014 | #81 | `INftProvider` with an offline mock provider and fixtures. |
| STORY-015 | #83 | Monad ownership provider: in-house JSON-RPC client and a Multicall3 `ownerOf` scan (DEC-001). |
| STORY-016 | #85, #87 | NFT metadata normalization and a Postgres cache over IPFS gateways (DEC-016). |
| STORY-017 | #89 | My Chogs gallery (`/chogs`) and one preferred character per player (DEC-017). |
| STORY-018 | #91 | `CharacterEligibilityService`: live ownership check and an immutable `CharacterSnapshot` at match start (DEC-019). |
| STORY-019 | #92 | `GameDefinitions` registry, `GameRoom`/`RoomMember` model, secure 6-character room codes (DEC-020). |
| STORY-020 | #94 | Create, join and read room API, one open room per player, invite links `/join/CODE`, minimal room page (DEC-021). |
| STORY-021 | #97 | Lobby: ready with the chosen Chog, host start with a live NFT check, room becomes Active (DEC-022). |
| STORY-063 | #99 | Leave a waiting room; the host leaving cancels it for everyone (DEC-023). Added during STORY-021. |
| STORY-022 | #103 | SignalR room hub: cookie-authenticated, membership checked before subscribing, leavers removed from groups; `RoomChanged` notices drive a live web lobby (DEC-024). |
| STORY-023 | #105 | Match state machine: Waiting → Active → Completed/Abandoned, Waiting → Cancelled; an Active match blocks other rooms; leaving a started match is a forfeit; character snapshots stored on room members (DEC-025). |
| STORY-024 | #107 | Match command engine: `POST /api/rooms/{code}/commands` with `actionId`, `matchId`, `expectedVersion`, `type` and `payload`; a room-wide version; retries replay their first result; stale commands are refused; per-game `IMatchRules`, none registered yet (DEC-026). |
| STORY-025 | #109 | Turns and AFK timer: seats in join order (host first), only the current player acts (`not_your_turn`, `turn_expired`), rules end turns, an in-process scheduler times out overdue turns exactly once with the game's safe default, and 3 missed turns in a row forfeit (DEC-027). |
| STORY-026 | #111 | Realtime sync: the hub pushes each member their own versioned room snapshot (`RoomUpdated`), and `SubscribeRoom` returns the current one for resync. Clients keep the newest version. Snapshots carry the game state through `IMatchRules.ViewFor`, filtered per player (DEC-028). |
| STORY-027 | #113 | Reconnect: `GET /api/rooms/current` finds the player's open room and a shell banner leads back to it; the web client reconnects forever with backoff, at once when back online or the tab is shown, and resyncs by subscribing. Being away never pauses the turn timer or earns a turn (DEC-029). |
| STORY-028 | #115 | Results and restarts: rules finish a match with standings, ranks stored once on the members with the move to Completed, stats updated in the same transaction (completed matches only, leavers count as played); on startup active matches resume with a full new turn and a bumped version (DEC-030). |
| STORY-029 | #117 | Randomness and security: `IRandomSource` (Core) reaches the rules in their command and timeout contexts, `SecureRandomSource` in production; seats are drawn in a secure random order at start; tests show forged rolls and player ids are ignored, retries can't reroll, and unauthorized room/turn/ability commands change nothing and draw nothing (DEC-031). |
| STORY-030 | #119 | Ability contract (Core): `AbilityDefinition`s in a per-game, versioned `AbilityCatalog` validated when built (one handler each, parameters in the handler's ranges); `AbilityEngine` uses an ability by id, checking player, payload, ability, seat target, activation window and the game's conditions before the effect. Handlers see ids and seats only, never characters or traits (DEC-032). |
| STORY-031 | #121 | Shared ability pool (Core): `IAbilityAssignmentStrategy` with `SharedAbilityPoolStrategy`, the only one, fixed in code; `AbilityPool` gives every player, guest or NFT holder, the game's whole catalog whatever their traits, never more than the catalog, and nothing for a game without one. Registered in DI; no API change (DEC-033). |
| STORY-032 | (this PR) | Trait-ready boundary (Core): `TraitSet` normalizes traits for matching (trimmed, whitespace collapsed, lower-case, distinct, sorted) and `ITraitAbilityResolver` is the contract for a future trait-based strategy (catalog + traits only, deterministic, whole catalog as fallback). No resolver ships; a test-only strategy swapped in through DI shows the engine, pool and games stay unchanged, and an architecture test keeps traits out of game modules and the engine (DEC-034). |

## Key architectural decisions

- **Shape:** a modular monolith. One API, one SPA, PostgreSQL, Docker Compose on a single VM, and no Kubernetes, microservices or cloud services. `Core` has no dependencies. `Infrastructure` and `Games.*` depend on `Core`, and `Api` depends on all of them; architecture tests enforce this.
- **Authority:** the server decides everything. The player always comes from the session cookie, never from request data. Player ids are never returned to clients; members are described by display name, avatar and flags such as `isHost` and `isYou`.
- **NFTs:**
  - Every NFT ownership check that admits a player (selecting an NFT, starting a match) reads the chain live.
  - Stored ownership only backs gallery listing during an outage of up to 10 minutes.
  - An ownership outage blocks starting with an NFT; there's no silent fallback to a standard Chog (DEC-019).
- **Abilities:** MVP uses `SharedAbilityPool` only. The lobby's ability field stays `null` until STORY-033.
  - Contract (DEC-032): `ChogGames.Core.Abilities`. Each game builds an `AbilityCatalog` (game type, version, definitions, one `IAbilityHandler` per ability, an `IAbilityTiming` for its activation windows); `AbilityCatalog.Create` throws on anything inconsistent. `AbilityEngine.Use` takes `{"abilityId", "target"}` (target = seat) and refuses with `not_playing`, `invalid_ability_payload`, `unknown_ability`, `invalid_target`, `ability_not_now` or the handler's own code, before any effect or draw.
  - Pool (DEC-033): `AbilityPool.For(gameType, character)` / `Offers(...)` over `IAbilityAssignmentStrategy`; `SharedAbilityPoolStrategy` (the only one, fixed in code) returns the whole catalog and never reads the character. `AbilityEngine`, the strategy and the pool are DI singletons; a game's abilities arrive by registering its `AbilityCatalog`.
  - Traits (DEC-034): `TraitSet` (`Core.Nfts`) and `ITraitAbilityResolver` (`Core.Abilities`) are the contract for a future trait-based strategy; nothing implements the resolver. Only a strategy may read a character's traits; `TraitAccessTests` (Architecture.Tests) scans game modules and Core's `Abilities`, `Games`, `Matches` and `Rooms` for trait access.
  - Not wired into `MatchCommandService` yet; no game registers a catalog. Selection, the selected-ability check, usage counting and routing `USE_ABILITY` are STORY-033/034.
- **Rooms:**
  - Supported games are seeded `GameDefinitions` rows; whether a game is enabled comes from `Games:Items` configuration.
  - A player can be in one open room at a time: a `Waiting` room (as host or member) or an `Active` match they haven't left.
  - Lifecycle (DEC-025): Waiting → Active (start) or Cancelled (host leaves); Active → Completed (game rules, later) or Abandoned (fewer than two players left). Every status change goes through `RoomLifecycle`; final states get `EndedAt`.
  - The room is the match: each `RoomMember` is the match player record, with its `CharacterSnapshot` (jsonb) and `LeftAt`.
  - Leaving a started match is a forfeit; the host has no special role after the start.
  - Rejoining a room changes nothing.
  - Ready is stored together with the chosen Chog.
  - The host starts once enough players are ready.
  - A host who leaves a waiting room cancels it.
- **Concurrency:** writes that must not race take Postgres row locks (`SELECT … FOR UPDATE`), always in the order room, then players sorted by id. The database backs the important rules with check constraints and filtered unique indexes.
- **Errors:**
  - Room APIs return problem details with a stable `code` such as `room_full` or `not_host`; clients rely on the code, never the text.
  - Unknown codes and rooms the caller isn't in both return `404 room_not_found`, so codes can't be probed.
- **Commands (DEC-026):**
  - Match commands go over REST, not the hub. Each runs under the room's row lock: replay check, then status, forfeit and `expectedVersion`, then the game's `IMatchRules`.
  - `GameRooms.Version` is bumped by every room change and is also an EF concurrency token. Game state is an opaque JSON object in `GameRooms.State`; applied actions are in `MatchActions` (PK room + action id).
  - Refusals aren't recorded. No game registers rules yet, so every command is `unknown_command`. Tests use a fake counter game.
- **Turns (DEC-027):**
  - The engine owns the turn: `GameRooms.CurrentPlayerId`, `TurnNumber`, `TurnEndsAt` and `TurnTimeout` (fixed per match at start); `RoomMembers.Seat` is the turn order (secure random since DEC-031) and `MissedTurns` the AFK count.
  - Command checks: replay, status, forfeit, `expectedVersion`, then current player (`not_your_turn`) and deadline (`turn_expired`), then the rules. `MatchRuleDecision.EndsTurn` passes the turn on; `IMatchRules.TimeOutTurn` gives the safe default (null skips).
  - `TurnTimeoutWorker` (Api, `BackgroundService`) calls `TurnTimeoutService` every `Matches:TimeoutCheckInterval`; each overdue room is re-checked under its row lock, so a turn times out exactly once. `Matches:MaxMissedTurns` (3) misses in a row forfeit.
- **Results (DEC-030):**
  - `MatchRuleDecision.Finish(state, standings)` ends the match: a rank for every player still playing (competition ranks, ties share). `GameRoom` checks them, moves to Completed and stores `RoomMember.Rank`; leavers rank after, the latest to leave highest. There is no results table.
  - `MatchResults.RecordStatsAsync` adds `GamesPlayed` for every member and `Wins` for rank 1, with atomic `ExecuteUpdate` after locking the players in id order, in the command's transaction. Abandoned matches change nothing.
  - Restart: `MatchRecoveryStartup` (an `IHostedService` registered before `TurnTimeoutWorker`) runs `MatchRecoveryService` before requests are served; each active match gets `TurnEndsAt = now + TurnTimeout` and a version bump, under its row lock.
- **Randomness (DEC-031):** all chance comes from `IRandomSource` (Core): `SecureRandomSource` (Infrastructure, `RandomNumberGenerator`) is a singleton in production, and rules get it as `MatchCommandContext.Random` / `MatchTurnContext.Random`. Payloads carry choices only; outcomes in them are ignored. `RandomSourceExtensions.Shuffle` is Fisher–Yates.
- **Realtime:** one SignalR hub (`/api/hubs/rooms`, WebSockets only).
  - After each commit, `RoomNotifier` reads the room again and pushes every subscribed player still playing their own snapshot (`RoomUpdated`, the same `RoomResponse` as `GET`) (DEC-028). `SubscribeRoom` returns the current snapshot, which is how a client resyncs after a gap.
  - Clients keep the highest `version` they've seen. Recipients come from the in-memory connection registry, not SignalR groups (single instance). Hub calls have their own per-player rate limit (DEC-024).
  - Reconnect (DEC-029): the cookie is the identity; `GET /api/rooms/current` finds the open room and the shell banner links to it. The web client (`RoomLiveUpdates`) replaces a dropped connection with a new one and never gives up (0, 2, 5, 10, then every 15 s; at once on `online` or the tab shown), and shows "offline" after 3 failures. The turn timer never pauses for a disconnected player.
  - Snapshots are built by `RoomSnapshots` (Api). The game state is only shown through `IMatchRules.ViewFor`; a game without rules never shows it.
- **Rate limits:** a per-player fixed window for each feature area, configured in settings such as `Rooms:RequestsPerMinutePerPlayer`.
- **Dependencies:** BouncyCastle (for keccak-256 and secp256k1) is the only approved crypto library. Wallets and NFTs use no SDKs. `@microsoft/signalr` (web) and `Microsoft.AspNetCore.SignalR.Client` (backend tests) are approved for realtime (DEC-024).

## Testing decisions

- **Backend:** xUnit.
  - Postgres integration tests use `[PostgresFact]` and `[PostgresTheory]`. They run only when `CHOG_TEST_POSTGRES` is set and are otherwise skipped silently, so always set it before claiming backend tests pass.
  - Helpers: `MigratedPostgresDatabase` (class fixture), `ApiFactory` with `.CreateHttpsClient()`, `ManualTimeProvider`, and `TestWallet` for signatures.
  - Hub tests (`RoomHubTests`) connect over TestServer WebSockets with the session cookie copied from a client created with `HandleCookies = false`.
  - External services (the chain, IPFS, the catalogue) are hand-written fakes inside the test classes; there are no mocking libraries.
  - Concurrency is tested with real parallel requests against Postgres. Those tests are run about 8 times in a row before a PR.
  - Each migration's check constraints get raw-SQL tests that try invalid rows. `RoomStatusOverride` forces a status together with the turn columns it needs.
  - Result tests (`MatchResultTests`) model a restart by disposing the `ApiFactory` and starting another on the same database; clients carry only the session cookie across.
  - Reconnect tests (`ReconnectTests`) model a browser refresh as a new client with only the session cookie, and drive turn timeouts by calling `TurnTimeoutService` directly.
  - Sync tests (`RoomSyncTests`) use two live hub clients and a fake game with per-player secrets to show convergence, resync and private views.
  - Chance is scripted: `ScriptedRandomSource` (in Core.Tests and Api.Tests) returns queued values, then the highest allowed, which keeps a shuffle's order. `ApiFactory` registers it by default, so seats stay in join order (host first) unless a test scripts otherwise.
  - Trait boundary tests: `TraitSetTests` (normalization), `TraitResolverBoundaryTests` (a test-only trait strategy over a fake resolver in the unchanged pool) and a DI swap in `AbilityPoolRegistrationTests`; `TraitAccessTests` with sample lines show the source rule reports trait access and ignores comments.
  - Ability tests (`Core.Tests/Abilities`) use `FakeAbilityGame` (a boost and a seat-targeted steal); `AbilityPoolTests` compare guests and NFT holders with different traits, and a fixed fake strategy shows the pool stays inside the catalog; a reflection test shows nothing in the handler-facing contract can reach characters, NFT traits or rooms.
  - Security tests (`MatchSecurityTests`) use a fake dice game: forged rolls and player ids are ignored, retries don't reroll, and every unauthorized command (outsider, other match, not started, not your turn, forfeited, expired, stale, closed; for `ROLL` and `USE_ABILITY`) changes nothing, records nothing, draws nothing and never reaches the rules.
  - Turn tests (`MatchTurnTests`) use a `ManualTimeProvider`, remove `TurnTimeoutWorker` and call `TurnTimeoutService` directly; one test keeps the worker to show it runs by itself.
  - CI checks that the EF model has no changes missing from a migration.
  - 3 live-chain tests are skipped unless explicitly enabled.
- **Web:** Vitest with `HttpTestingController`; specs drive the page through its rendered DOM.
- **Workspace:** `npm run test:workspace` (node:test) covers the skeleton, Compose config and the secret scan. `npm run deps:verify` is the Compose smoke test.
- **Before each PR:** a Release build with 0 warnings, all suites, web lint/test/build, and a live smoke test through the dev proxy (`:4200`) with curl and cookie jars.

## Conventions

- **Stories:**
  - One backlog story per branch and per PR.
  - Branch `feature/story-<nnn>-<kebab-title>` from `develop`; the PR goes into `develop` with `Closes #<issue>` and the PR template.
  - Commits start with `STORY-<nnn>:`.
  - The product owner merges and then says "proceed".
- **Decisions:** open product questions are asked before coding, each with a recommended option. The answers become a `DEC-0xx` entry in `docs/decisions.md`.
- **Story status:** a story is set to `Done` in the backlog only when its acceptance criteria are verified. README sections are updated for API, config and migration changes.
- **New stories:** appended with the next free number (latest: STORY-063), placed in their epic, with a GitHub issue using the same body format, labelled `story`, `epic:E0x`, `priority:Px` and given the epic milestone.
- **Code:** problem-details errors with `code`; endpoint groups use `.RequireAuthorization().RequireRateLimiting(...)`; options are validated with `ValidateOnStart`; results are records with an outcome enum; doc comments cite requirement, story and decision ids.

## Known issues and tech debt

- **Completing a match:** only a command can finish a match (`MatchRuleDecision.Finish`); no game has rules yet, so nothing completes in practice. The 20-minute cap (ENG-011) will need its own way to finish a match with the game's timeout scoring.
- **Results page:** the room page shows each member's place and a "you won" / "you finished Nth" line; there's no match history list or game-specific result details (LIFE-010) yet.
- **Restart resume runs at every start:** including a quick restart with seconds left on a turn, which then gets a full turn. If the database is down at startup, matches aren't resumed and their turns time out as before.
- **Out-of-turn actions:** only the current player can act. Reactions by other players (Chog Race, DEC-003) will need the rules to allow them.
- **Turn scheduler is single instance:** like the hub registry, it runs in-process; scale-out needs one runner (e.g. an advisory lock).
- **Dev log noise:** in Development, EF logs the overdue-turn query every second.
- **No foreign key on `GameRooms.CurrentPlayerId`:** the domain keeps it a playing member; a composite FK to `RoomMembers` would create an insert cycle in EF.
- **Matches started before STORY-023** have no character snapshots; the room shows their current Chog, and they can still be left. Migration `AddMatchLifecycle` reclassifies a started-then-cancelled room as `Abandoned`, so rolling it back and forward is lossless.
- **No presence indicator:** other players can't see who is disconnected (DEC-029); the turn timer handles absent players.
- **Offline hint after a refused subscription:** a player who forfeited sees "still trying to reconnect" although the client has stopped; the page also says they left.
- **Started rooms in the lobby UI:** the web shows "The game has started" and a Leave game button; there's no game screen yet.
- **Snapshot pushes read the room once per change:** with several quick changes each push reads the latest state, so clients may see a version skipped or the same version twice; that's harmless, since snapshots are complete.
- **A waiting-room leaver's other tabs** aren't told they left; they show the lobby until reloaded (any action answers `room_not_found`).
- **Live connections keep their identity:** a hub connection keeps the player it was opened with, even after sign-out or a wallet switch, until it reconnects. It only ever gets notices for rooms that player is in.
- **Single instance realtime:** the connection registry is in memory; scale-out needs a backplane and shared tracking.
- **Lobby display:** NFT Chogs show as `Chog #<id>` without an image.
- **Start holds a lock during chain calls:** start runs up to six live ownership checks while holding the room's row lock. That's acceptable at this scale, but worth revisiting if the chain is slow.
- **Duplicated code:** `FindCurrentPlayerAsync` is repeated in each endpoint class. (The room-lock query is now shared in `RoomLocks`.)
- **Commands return versions only:** the new state arrives as the pushed snapshot (or a `GET`).
- **Action history grows per match:** `MatchActions` keeps every applied action; there's no clean-up or retention policy yet.
- **Formatting:** Prettier isn't enforced; about 54 files differ from its formatting.
- **npm:** npm 10.9 crashes on `npm install` of new packages. Use `npx npm@11 install …` (`npm ci` is fine).
- **Mock NFT ownership** isn't exclusive: every wallet that isn't a fixture sees the same tokens.
- **Deferred:** WalletConnect (DEC-014) and smart-contract wallet (ERC-1271) sign-in.
- **Pending decisions:**
  - DEC-002: Chog Life rules.
  - DEC-003: Chog Race reactions.
  - DEC-004: merging guest and wallet histories.
  - DEC-005: branding and artwork.
  - DEC-018: avatar uploads, needed before STORY-062.

## What's next

1. **STORY-033 — Lobby ability selection and lock** (E05 abilities, STORY-033 and 034).

Dev environment notes:
- Start everything with `NX_DAEMON=false npm run start:all`: web on `:4200`, proxying `/api` to the API on `:5248`.
- After changing the API, restart it; it doesn't hot-reload.
