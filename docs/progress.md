# Progress

Snapshot of where the project stands, for picking work up in a new session. Source of truth for scope is
`docs/backlog.md`; for decisions, `docs/decisions.md`. Last updated 2026-10-12 (STORY-024).

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
| STORY-024 | (this PR) | Match command engine: `POST /api/rooms/{code}/commands` with `actionId`, `matchId`, `expectedVersion`, `type` and `payload`; a room-wide version; retries replay their first result; stale commands are refused; per-game `IMatchRules`, none registered yet (DEC-026). |

## Key architectural decisions

- **Shape:** a modular monolith. One API, one SPA, PostgreSQL, Docker Compose on a single VM, and no Kubernetes, microservices or cloud services. `Core` has no dependencies. `Infrastructure` and `Games.*` depend on `Core`, and `Api` depends on all of them; architecture tests enforce this.
- **Authority:** the server decides everything. The player always comes from the session cookie, never from request data. Player ids are never returned to clients; members are described by display name, avatar and flags such as `isHost` and `isYou`.
- **NFTs:**
  - Every NFT ownership check that admits a player (selecting an NFT, starting a match) reads the chain live.
  - Stored ownership only backs gallery listing during an outage of up to 10 minutes.
  - An ownership outage blocks starting with an NFT; there's no silent fallback to a standard Chog (DEC-019).
- **Abilities:** MVP uses `SharedAbilityPool` only. The lobby's ability field stays `null` until STORY-033.
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
- **Realtime:** one SignalR hub (`/api/hubs/rooms`, WebSockets only). It only says `RoomChanged(code)`; clients refetch the REST snapshot. Subscribing checks membership in the database; leavers are removed from the group via an in-memory connection registry (single instance). Hub calls have their own per-player rate limit (DEC-024).
- **Rate limits:** a per-player fixed window for each feature area, configured in settings such as `Rooms:RequestsPerMinutePerPlayer`.
- **Dependencies:** BouncyCastle (for keccak-256 and secp256k1) is the only approved crypto library. Wallets and NFTs use no SDKs. `@microsoft/signalr` (web) and `Microsoft.AspNetCore.SignalR.Client` (backend tests) are approved for realtime (DEC-024).

## Testing decisions

- **Backend:** xUnit.
  - Postgres integration tests use `[PostgresFact]` and `[PostgresTheory]`. They run only when `CHOG_TEST_POSTGRES` is set and are otherwise skipped silently, so always set it before claiming backend tests pass.
  - Helpers: `MigratedPostgresDatabase` (class fixture), `ApiFactory` with `.CreateHttpsClient()`, `ManualTimeProvider`, and `TestWallet` for signatures.
  - Hub tests (`RoomHubTests`) connect over TestServer WebSockets with the session cookie copied from a client created with `HandleCookies = false`.
  - External services (the chain, IPFS, the catalogue) are hand-written fakes inside the test classes; there are no mocking libraries.
  - Concurrency is tested with real parallel requests against Postgres. Those tests are run about 8 times in a row before a PR.
  - Each migration's check constraints get raw-SQL tests that try invalid rows.
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

- **Completing a match:** `GameRoom.Complete` exists but nothing calls it yet; game rules (Chog Life/Race) and the 20-minute cap (ENG-011) will. Results and stats are STORY-028.
- **Stuck matches:** an `Active` match whose players all go silent stays Active and blocks them from other rooms until they leave it. AFK handling (STORY-025) and the time cap should end such matches.
- **Matches started before STORY-023** have no character snapshots; the room shows their current Chog, and they can still be left. Migration `AddMatchLifecycle` reclassifies a started-then-cancelled room as `Abandoned`, so rolling it back and forward is lossless.
- **Started rooms in the lobby UI:** the web shows "The game has started" and a Leave game button; there's no game screen yet.
- **Lobby updates:** each `RoomChanged` notice costs every viewer one `GET`, which counts against the per-player room limit. Versioned snapshots come with STORY-026.
- **Live connections keep their identity:** a hub connection keeps the player it was opened with, even after sign-out or a wallet switch, until it reconnects. It only ever gets notices for rooms that player is in.
- **Single instance realtime:** the connection registry is in memory; scale-out needs a backplane and shared tracking.
- **Lobby display:** NFT Chogs show as `Chog #<id>` without an image.
- **Start holds a lock during chain calls:** start runs up to six live ownership checks while holding the room's row lock. That's acceptable at this scale, but worth revisiting if the chain is slow.
- **Duplicated code:** `FindCurrentPlayerAsync` is repeated in each endpoint class. (The room-lock query is now shared in `RoomLocks`.)
- **Match state isn't sent to clients yet:** commands return versions only; filtered, versioned snapshots are STORY-026 (ENG-013 privacy).
- **Action history grows per match:** `MatchActions` keeps every applied action; there's no clean-up yet (results and retention are STORY-028).
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

1. **STORY-025 — Turn and AFK timer:** the server controls the current player, a configurable turn timeout and a deterministic fallback.
2. **Then:** STORY-026 to 029, then E05 abilities (STORY-030 to 034).

Dev environment notes:
- Start everything with `NX_DAEMON=false npm run start:all`: web on `:4200`, proxying `/api` to the API on `:5248`.
- After changing the API, restart it; it doesn't hot-reload.
