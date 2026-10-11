# Progress

Snapshot of where the project stands, for picking work up in a new session. Source of truth for scope is
`docs/backlog.md`; for decisions, `docs/decisions.md`. Last updated 2026-10-12 (after PR #99).

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
  - A player can be in one open (`Waiting`) room at a time, as host or member.
  - Rejoining a room changes nothing.
  - Ready is stored together with the chosen Chog.
  - The host starts once enough players are ready.
  - A host who leaves cancels the room.
- **Concurrency:** writes that must not race take Postgres row locks (`SELECT … FOR UPDATE`), always in the order room, then players sorted by id. The database backs the important rules with check constraints and filtered unique indexes.
- **Errors:**
  - Room APIs return problem details with a stable `code` such as `room_full` or `not_host`; clients rely on the code, never the text.
  - Unknown codes and rooms the caller isn't in both return `404 room_not_found`, so codes can't be probed.
- **Rate limits:** a per-player fixed window for each feature area, configured in settings such as `Rooms:RequestsPerMinutePerPlayer`.
- **Dependencies:** BouncyCastle (for keccak-256 and secp256k1) is the only approved crypto library. Wallets and NFTs use no SDKs.

## Testing decisions

- **Backend:** xUnit.
  - Postgres integration tests use `[PostgresFact]` and `[PostgresTheory]`. They run only when `CHOG_TEST_POSTGRES` is set and are otherwise skipped silently, so always set it before claiming backend tests pass.
  - Helpers: `MigratedPostgresDatabase` (class fixture), `ApiFactory` with `.CreateHttpsClient()`, `ManualTimeProvider`, and `TestWallet` for signatures.
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

- **Character snapshot:** checked at start but not stored yet; it belongs on the match player record (STORY-023/028).
- **Started matches:** an `Active` match doesn't stop a player from joining or creating another room (DEC-021; decide in STORY-023). Players also can't leave an Active match yet (STORY-023/025/027).
- **Lobby updates:** refresh only; live updates come with SignalR (STORY-022/026).
- **Lobby display:** NFT Chogs show as `Chog #<id>` without an image.
- **Start holds a lock during chain calls:** start runs up to six live ownership checks while holding the room's row lock. That's acceptable at this scale, but worth revisiting if the chain is slow.
- **Duplicated code:**
  - `FindCurrentPlayerAsync` is repeated in each endpoint class.
  - The room-lock query is repeated in `RoomService` and `LobbyService`.
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

1. **STORY-022 — SignalR auth and groups:** hub connections authenticated with the session cookie, room membership checked before subscribing, and group messages reaching members only. Live lobby updates then replace the Refresh button.
2. **STORY-023 — Authoritative match state machine:** settle the Active-room rule, leaving a started match, and storing the character snapshot.
3. **Then:** STORY-024 (command envelope and idempotency), STORY-025 (turn and AFK timer), STORY-026 to 029, then E05 abilities (STORY-030 to 034).

Dev environment notes:
- Start everything with `NX_DAEMON=false npm run start:all`: web on `:4200`, proxying `/api` to the API on `:5248`.
- After changing the API, restart it; it doesn't hot-reload.
