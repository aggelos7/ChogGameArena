# Chog Game Arena

Mobile-first multiplayer browser games for the Chog community on Monad. This repository contains the planning pack (requirements, ordered backlog of 61 stories, architecture, decision log and agent instructions) and the Nx workspace skeleton the application is built in.

## Repository tooling

Nx is the frontend monorepo/task orchestration tool. Angular lives in `apps/web`; shared TypeScript libraries live in `libs/`; the .NET 8 solution lives in `backend/` and remains buildable with native `dotnet` commands. Nx Cloud is not used (`neverConnectToCloud` in `nx.json`); task results are cached locally in `.nx/cache`. Docker Compose remains the local and VM deployment model.

```text
apps/web/        Angular 22 SPA (standalone, zoneless, esbuild, Vitest)
libs/            Shared browser-side TypeScript libraries
backend/         Native .NET 8 solution (ChogGames.sln: Api, Core, Games.*, Infrastructure)
infrastructure/  Docker Compose for local PostgreSQL and Redis (deployment assets later)
docs/            Requirements, architecture, backlog, decisions
tools/           Workspace scripts and checks
```

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| Git | any recent | |
| Node.js 22 (LTS) | `>=22.22.3 <23.0.0`, see `.nvmrc` | `npm ci` refuses other versions (`engine-strict`) |
| npm | `>=10` | ships with Node.js 22 |
| .NET 8 SDK | 8.0.x | required from STORY-002 |
| Docker with Compose | v2 (`docker compose`) | local PostgreSQL and Redis |

Nx `23.3.0` and Angular `22.2.1` are pinned to exact versions in `package.json` and `package-lock.json`; no global Nx/Angular CLI install and no cloud account are needed.

> **Changing dependencies:** `npm ci` works with the npm 10 bundled with Node.js 22, but `npm install` of new/updated packages crashes in npm 10.9 (`Cannot read properties of null (reading 'edgesOut')`). Use `npx npm@11 install <pkg>@<exact-version>` when adding or upgrading packages, and commit the resulting lockfile.

## Local commands

```bash
nvm use                 # optional: switch to the Node.js version in .nvmrc
npm ci                  # install the exact locked dependencies
npx nx --version        # verify the local, pinned Nx CLI
npm run test:workspace  # run workspace skeleton checks (node:test)
```

### Frontend

```bash
npm start               # dev server on http://localhost:4200 (same as npx nx serve web); /api is proxied to http://localhost:5248
npm run start:all       # API (Development) and web dev server together
npx nx build web        # production build to dist/apps/web
npx nx test web         # unit tests (Vitest via @angular/build:unit-test)
npx nx lint web         # ESLint (angular-eslint)
npx nx g @schematics/angular:component <path> --project=web   # Angular schematics via Nx
```

Routes: `/` (home), `/games` (game catalogue), `/games/:gameId` (lobby placeholder, only for `Available` games; anything else redirects to `/games`); unknown URLs redirect to `/`.

Mobile-first shell: layouts are designed for 375px phones first (no horizontal scroll, controls at least 44px tall); the main navigation becomes a row of full-width tabs on phones and a single header row from 40rem. Everything works with the keyboard alone: a "Skip to main content" link, visible focus outlines, `aria-current="page"` on the active link, and focus moved to the page content after each in-app navigation. Pages use the shared loading, error (with retry) and empty states in `apps/web/src/app/shared/ui/state-views.ts`; a page that fails to download shows a "Reload page" error and keeps the current page.

### Local dependencies (PostgreSQL and Redis)

`infrastructure/docker-compose.yml` runs PostgreSQL 18.6 and Redis 8.10.2 (pinned images) with named volumes (`postgres-data`, `redis-data`), health checks and ports bound to `127.0.0.1` only.

```bash
cp infrastructure/.env.example infrastructure/.env   # once; set a local password (file is git-ignored)
npm run deps:up      # start and wait until both services are healthy
npm run deps:down    # stop and remove containers; data volumes are kept
npm run deps:verify  # smoke test: isolated stack on ports 15432/16379, checks data survives a restart, then cleans up
```

| Setting (`infrastructure/.env`) | Default | Notes |
|---|---|---|
| `POSTGRES_PASSWORD` | *(required)* | Compose refuses to start without it |
| `POSTGRES_DB` / `POSTGRES_USER` | `choggames` | |
| `POSTGRES_PORT` | `5432` | change if the port is already in use |
| `REDIS_PORT` | `6379` | Redis has no password locally; it is reachable from this machine only |

To wipe local data: `docker compose -f infrastructure/docker-compose.yml down -v`.

### Configuration and secrets

Backend settings are bound to typed options and validated at startup; a missing or invalid required setting stops the API with a message naming the setting. Secrets never go in `appsettings*.json` or any committed file (`npm run test:workspace` scans for them).

| Setting | Required | Description |
|---|---|---|
| `Database:ConnectionString` | yes | Npgsql connection string; must include `Host`, `Database` and `Username` |

Provide values, in order of preference:

```bash
# Local development: .NET user-secrets (stored in ~/.microsoft/usersecrets, outside the repo; Development environment only)
dotnet user-secrets set "Database:ConnectionString" \
  "Host=localhost;Port=5432;Database=choggames;Username=choggames;Password=<password>" \
  --project backend/src/ChogGames.Api

# Containers / CI: environment variables (':' becomes '__'); template in backend/.env.example
export Database__ConnectionString="Host=<host>;Database=<db>;Username=<user>;Password=<password>"
```

Use the same password as `POSTGRES_PASSWORD` in `infrastructure/.env`.

### Backend

Native `dotnet` commands are authoritative; the SDK is pinned in `backend/global.json` and package versions in `backend/Directory.Packages.props`.

```bash
dotnet build backend/ChogGames.sln   # build all backend projects
dotnet test backend/ChogGames.sln    # run backend tests, incl. architecture dependency rules
dotnet run --project backend/src/ChogGames.Api   # API on http://localhost:5248 (needs Database:ConnectionString)
npm run db:migrate                   # apply EF Core migrations (uses Database__ConnectionString or user-secrets)
npx nx run backend:test              # same commands via Nx (uncached wrapper)
```

Game catalogue (`GET /api/games`): games and their status come from the non-secret `Games:Items` configuration in `appsettings.json` and are validated at startup (unique kebab-case `Id`, required `Name`, `Status` one of `Available`, `ComingSoon`, `Maintenance`). Both games default to `ComingSoon`; `appsettings.Development.json` makes Chog Life `Available` locally. Only `Available` games can be launched; the web app re-checks the server before opening a game.

```json
[{ "id": "chog-life", "name": "Chog Life", "description": "...", "status": "ComingSoon" }]
```

Guest identity (SUITE-002, ADR-005): the web app calls `GET /api/me` on startup and, only on `401`, `POST /api/guest-sessions`. The server creates a `Player` (opaque server-generated id, display name) and sets the session in an **HttpOnly, SameSite=Lax** cookie `chog_session` (Secure outside local development). Only the SHA-256 hash of the 256-bit token is stored. Identity comes **only** from that cookie; player ids in bodies, queries or headers are ignored.

| Endpoint | Request | Responses |
|---|---|---|
| `POST /api/guest-sessions` | `{ "displayName"?: string }` (3–20 letters, digits, spaces, `_`, `-`; generated as `Chog 1234` if omitted) | `201` new guest + cookie; `200` existing guest when the cookie is still valid; `400` invalid name (nothing created); `429` over the per-IP limit |
| `GET /api/me` | session cookie | `200 { playerId, displayName, kind }`; `401` without a valid session |

| Setting (`GuestSessions:`) | Default | |
|---|---|---|
| `Lifetime` | `90.00:00:00` | session validity, extended on use |
| `RenewInterval` | `1.00:00:00` | at most one renewal write per interval |
| `RequireSecureCookie` | `true` | `false` only in `appsettings.Development.json` (plain HTTP) |
| `CreatePerMinutePerClient` | `10` | guest creations per client IP per minute |

Player profile (SUITE-004): always the caller's own profile — there is no player id in any route or body, so one player can never edit another's.

| Endpoint | Request | Responses |
|---|---|---|
| `GET /api/profile` | session cookie | `200 { playerId, displayName, kind, avatar: { id, name, color }, gamesPlayed, wins }`; `401` |
| `PATCH /api/profile` | `{ "displayName"?: string, "avatarId"?: string }` | `200` updated profile; `400` invalid name, unknown avatar or no editable field (nothing changes); `401` |
| `GET /api/profile/avatars` | session cookie | `200 [{ id, name, color }]` built-in placeholder avatars (`classic` default, `mint`, `sunset`, `ocean`, `berry`, `gold`) |

`gamesPlayed`/`wins` are maintained by the server only (database check: `0 <= Wins <= GamesPlayed`); values sent by clients are ignored. Avatars are coloured placeholders until official artwork is approved (DEC-005).

Wallet connection (STORY-012, DEC-014): the Profile page's Wallet section discovers browser wallets via EIP-6963 (falling back to `window.ethereum`), connects with `eth_requestAccounts`, shows the wallet, short address and network, prompts a switch when the wallet is on another chain (adding the chain to the wallet if needed), and disconnects by clearing all wallet state and asking the wallet to revoke the site's permission. No wallet SDK is used. Every wallet response is validated; anything malformed leaves the app disconnected. Connecting alone is **not** authentication; see wallet sign-in below.

| Endpoint | Responses |
|---|---|
| `GET /api/wallet/chain` | `200 { chainId, name, nativeCurrency: { name, symbol, decimals }, rpcUrls, blockExplorerUrls }`; no session needed |

| Setting (`Wallet:Chain:`) | Default (`appsettings.json`, all environments) |
|---|---|
| `ChainId` | `143` (Monad Mainnet) |
| `Name` | `Monad Mainnet` |
| `CurrencyName` / `CurrencySymbol` / `CurrencyDecimals` | `Monad` / `MON` / `18` |
| `RpcUrls` | `https://rpc.monad.xyz` |
| `BlockExplorerUrls` | `https://monadvision.com` |

Development uses Monad Mainnet too, because Chog NFTs exist only there. Sign-in is a free signature and the app never sends transactions.

These values are sent to browsers, so URLs must be public `https://` addresses; credentials and query strings are rejected at startup. Never put a keyed RPC provider URL here. Lists override by index, so keep the same number of entries per environment or override every index.

Wallet sign-in (STORY-013, SUITE-003, DEC-015): connecting a wallet is **not** authentication. In the Wallet section, "Sign in with wallet" works like this:
1. The app asks the server for a one-time EIP-4361 message (domain, URI, chain, address, nonce, 5-minute expiry), built entirely from server configuration.
2. The wallet signs the message (`personal_sign`; free, not a transaction).
3. The server recovers the signer and compares it with the challenged address.

On success the session cookie is replaced; the old cookie stops working. Each challenge works once, only for the session that requested it, and is used up even by a failed attempt. Replayed, expired, altered (e.g. wrong-domain) or wrongly-signed messages are rejected and change nothing. Only regular (EOA) wallets are supported; smart-contract wallets fail safely.

| Endpoint | Request | Responses |
|---|---|---|
| `POST /api/wallet/challenge` | session cookie; `{ "address": "0x…" }` | `200 { challengeId, message, expiresAt }`; `400` invalid address; `401`; `429` |
| `POST /api/wallet/verify` | session cookie; `{ "challengeId", "signature" }` | `200 { playerId, displayName, kind: "Wallet", walletAddress }` + new cookie; `400` with `errors.challengeId` (expired/used/not yours) or `errors.signature` (not signed by that address over that message); `401`; `429` |
| `POST /api/wallet/sign-out` | session cookie | `204`, session revoked and cookie cleared; the app then starts a new guest |

`GET /api/me` also returns `walletAddress` (`null` for guests). Binding: a new wallet upgrades the current guest (same player); a wallet already bound to a player signs in as that player.

| Setting (`WalletAuth:`) | Default | |
|---|---|---|
| `Domain` | *(required)* | host[:port] users see the web app at; `localhost:4200` in `appsettings.Development.json` |
| `Uri` | *(required)* | web app origin, e.g. `https://app.example.com` (`http` only for localhost); must match `Domain` |
| `ChallengeLifetime` | `00:05:00` | 1-15 minutes |
| `RequestsPerMinutePerClient` | `20` | challenge + verify requests per client IP per minute |

Every deployment must set `WalletAuth:Domain` and `Uri` to the address users open; the API refuses to start without them. Wallets show this domain and warn when it doesn't match the page.

NFT ownership (STORY-014/015, ADR-006, DEC-001): server code reads Chog NFTs through `INftProvider` (Core). It lists a wallet's NFTs, or checks whether a wallet owns one token. Callers pass only the server-verified wallet address from the session, never one from a request. Tokens are normalized: a canonical uint256 id, a name, an optional image (`null` when unusable) and any trait types and values, known or not. The NFT endpoints are under Character gallery below.

| Setting (`Nfts:`) | Default (`appsettings.json`) | |
|---|---|---|
| `Provider` | `Monad` | `Monad`: reads the chain (below). `Mock`: offline fixtures, set in `appsettings.Development.json`; refused in Production. `None`: every lookup fails as unavailable, so ownership is never assumed |
| `Mock:OtherWallets` | `Many` | what any non-fixture wallet (e.g. your own signed-in wallet) owns under `Mock`: `None`, `One` or `Many` |
| `Monad:RpcUrl` | `https://rpc.monad.xyz` | JSON-RPC endpoint; a keyed provider URL belongs in user-secrets or environment variables, never in git. It is never logged |
| `Monad:ChainId` | `143` | the endpoint must report this chain before any answer is used |
| `Monad:ContractAddress` | `0xc96d31f8626c6d03fae5dcd3d61e3fb9f4a73763` | official Chog collection (DEC-001) |
| `Monad:Multicall3Address` | `0xcA11bde05977b3631167028862bE2a173976CA11` | standard Multicall3 deployment, used only for listing |
| `Monad:FirstTokenId` / `LastTokenId` | `1` / `1969` | token range that is scanned |
| `Monad:BatchSize` | `500` | `ownerOf` calls per Multicall3 request |
| `Monad:OwnershipCacheDuration` | `00:00:30` | how long a listing scan is reused (0–10 min) |
| `Monad:RequestTimeout` | `00:00:10` | per RPC request (1 s – 1 min) |

How the Monad provider reads ownership:
- **Listing:** the collection isn't ERC-721 Enumerable, and the public RPC limits log queries to 100 blocks. The provider therefore reads `ownerOf` for every token in the range through Multicall3: 4 read-only calls, about 0.6 s. Wallets are matched against that scan, which is reused for 30 s.
- **Single-token checks** (for match admission) call `ownerOf` on the Chog contract directly every time and never use the scan.
- **Failures:** RPC errors, timeouts, rate limits, malformed answers or a wrong chain raise `NftProviderUnavailableException`. They never report "owns nothing" or "owns it".

Stored ownership and metadata (STORY-016, DEC-016): with `Monad`, answers are saved in PostgreSQL and metadata is added.
- **Tables:** `ChogNfts` holds the owner and when it was checked, plus metadata status, name and `ipfs://` image. `ChogNftTraits` holds traits in order. `NftOwnershipScans` records each wallet's last successful scan.
- **Owners:** every live answer is saved. A token that left a wallet loses its stored owner, and older writes never overwrite newer ones.
- **Metadata:** `tokenURI` is read on chain. Only `ipfs://` URIs are followed, through the configured gateways in order, with no redirects and at most 64 KiB.
  - Normalization keeps any trait type and value; numbers and booleans become text, and unusable entries are skipped. An unsafe or missing image becomes no image, and a document that isn't a JSON object is stored as `Invalid`.
  - A request fetches missing or stale metadata for at most `MetadataFetchBudget`. Tokens not reached show as `Chog #<id>` without an image and are filled in by a later request. Bad metadata never fails a request.
  - Metadata is read again weekly; outages are retried after 5 minutes. An outage never erases metadata that was already read.
- **RPC outage:** a wallet's list comes from its stored scan if that scan is at most `OwnershipFallback` old. Otherwise the request fails as unavailable. A wallet that was never scanned is never reported as owning nothing, and single-token checks never use stored ownership.
- **Images:** browsers receive image URLs through the first gateway.

| Setting (`Nfts:`) | Default | |
|---|---|---|
| `Ipfs:Gateways` | `https://gateway.pinata.cloud/ipfs/` | gateway bases ending in `/ipfs/`, tried in order; a dedicated gateway URL with a key belongs in secrets and is never logged |
| `Ipfs:RequestTimeout` | `00:00:10` | per gateway request |
| `Cache:OwnershipFallback` | `00:10:00` | 0 – 1 h |
| `Cache:MetadataRefreshInterval` | `7.00:00:00` | |
| `Cache:MetadataRetryInterval` | `00:05:00` | after a gateway or RPC outage |
| `Cache:MetadataFetchBudget` | `00:00:05` | metadata time per request |
| `Cache:MaxConcurrentMetadataFetches` | `4` | |

To read the real chain locally, run `dotnet user-secrets set "Nfts:Provider" "Monad" --project backend/src/ChogGames.Api`. Live tests (`MonadLiveTests`) run only when `CHOG_TEST_MONAD_RPC` is set, e.g. `CHOG_TEST_MONAD_RPC=https://rpc.monad.xyz`; the metadata one also needs `CHOG_TEST_POSTGRES`.

The mock provider needs no RPC or network; images are inline SVG. Its fixture wallets (`MockNftFixtures`) are made-up addresses:

| Wallet | Owns |
|---|---|
| `0x…c000` | nothing |
| `0x…c001` | token 1 |
| `0x…c002` | tokens 7, 42 (no image), 108 (unknown and non-ASCII traits, maximum-length value) and 1969 (no traits) |
| `0x…c003` | every lookup fails as if the RPC were down |

Character gallery (STORY-017, NFT-002/004/005/006): the web app's **My Chogs** page (`/chogs`) lists the signed-in wallet's verified Chog NFTs and the standard Chogs, and saves the one character the player uses next. Standard Chogs are the built-in avatars (`classic` … `gold`), available to everyone, drawn from their colour until artwork is approved (DEC-005).

| Endpoint | Request | Response |
|---|---|---|
| `GET /api/characters` | session cookie | `200 { nftStatus, nfts: [{ tokenId, name, imageUrl, traits: [{ type, value }] }], standard: [{ id, name, color }], selected: { kind, id } }`; `401`; `429` |
| `PUT /api/characters/selected` | session cookie; `{ "kind": "Standard" \| "Nft", "id": string }` | `200 { kind, id }`; `400` with `errors.kind` (unknown kind, or an NFT for a guest) or `errors.id` (unknown standard Chog, malformed token id, or a token the wallet doesn't own); `503` ownership can't be checked; `401`; `429`. A refused request changes nothing |

- **`nftStatus`:** `Available` (the list may be empty), `NoWallet` for guests, or `Unavailable` when ownership can't be read. Standard Chogs are always listed.
- **Token ids** are decimal strings (uint256), never numbers.
- **The NFT list** comes only from the server's ownership check of the session's wallet. Choosing an NFT reads `ownerOf` live; the browser's list is never trusted.
- **`selected`** is the stored preference. A stored NFT that the wallet was just shown not to own any more is reported as `classic`. During an outage the stored choice is shown as is. Ownership is verified again when a match starts (STORY-018).
- **Database:** `Players.SelectedCharacterKind` / `SelectedCharacterId` (migration `AddPlayerSelectedCharacter`). A check constraint allows an NFT choice only for wallet players.

| Setting (`Nfts:`) | Default | |
|---|---|---|
| `RequestsPerMinutePerPlayer` | `30` | gallery and selection requests per player per minute (1–1000); each may read the chain |

Match character check (STORY-018, NFT-008/009, DEC-019): `CharacterEligibilityService.SnapshotForMatchAsync(player)` is what match start calls (STORY-023) to fix each player's character. There is no endpoint for it.
- **Standard Chog:** always eligible; no chain call.
- **NFT:** ownership is read live with `FindOwnedAsync` every time, never from stored ownership or the gallery. The results are:
  - `Eligible` with a snapshot;
  - `NftNotOwned`: the player must choose again;
  - `OwnershipUnavailable`: the chain can't be read, so the match doesn't start with that NFT, though a standard Chog still works.
- **Snapshot:** an immutable `CharacterSnapshot`: kind, id, name, image, a copy of the traits, the verified wallet and the time. Later transfers, metadata refreshes or a new gallery choice don't change it. Each match takes a new one, so ownership is checked again.
- **Selection:** the player's saved choice is never changed by this check.
- **Storage:** the snapshot is stored with the match player record when matches exist (STORY-023/028).

Mock ownership isn't exclusive: every other wallet reports the same fixture tokens.

Game registry and rooms (STORY-019/020, ENG-001, DEC-020/021): every room request runs as the session's player; player ids are never accepted from or returned to clients.
- **Registered games:** the `GameDefinitions` table, seeded by migration with `chog-life` and `chog-race` (2–6 players each). A new game needs a migration and a `Games:Items` entry.
- **Enabled games:** the `Games:Items` status; only `Available` games get rooms.
- **Room:** status `Waiting`, the creator as host and first member, capacity from the game (6).
- **Code:** 6 characters from `23456789ABCDEFGHJKMNPQRSTUVWXYZ` (no 0, O, 1, I or L), drawn with secure randomness, unique in the database and redrawn on a collision (up to 5 times). Input is accepted in any letter case. The invite link is `/join/<code>` in the web app.
- **One open room per player:** a player in a `Waiting` room, as host or member, can't create or join another. Each create or join locks the player's row (and, for a join, the room's row first), so concurrent requests can't get round this or overfill a room.

| Endpoint | Request | Responses |
|---|---|---|
| `POST /api/rooms` | `{ "gameType": "chog-life" }` | `201` room, `Location: /api/rooms/<code>`; `400 unknown_game`; `409 game_unavailable` or `already_in_room` |
| `POST /api/rooms/{code}/join` | — | `200` room (also when already a member, nothing changes); `404 room_not_found`; `409 room_full`, `room_started`, `room_closed` or `already_in_room` |
| `GET /api/rooms/{code}` | — | `200` room for members; `404 room_not_found` for everyone else |

All three need a session (`401` without) and return `429` over the per-player limit. A room is `{ code, gameType, status, capacity, members[{ displayName, avatarId, isHost, isYou }] }`. Errors are problem details with a stable `code` (clients rely on it, never on the text); `already_in_room` also carries `roomCode`, the room the player is in.

| Setting (`Rooms:`) | Default | |
|---|---|---|
| `RequestsPerMinutePerPlayer` | `60` | room requests per player per minute (1–1000); also limits guessing codes |

Health endpoints:

| Endpoint | Checks | Healthy | Unhealthy |
|---|---|---|---|
| `GET /health/live` | none (process is running) | `200` | — |
| `GET /health/ready` | PostgreSQL connection (5 s timeout) | `200` | `503` |

Responses are JSON (`status`, per-check `name`/`status`/`durationMs`) and never include exception details or connection information.

Migrations (EF Core 8, `ChogGames.Infrastructure/Persistence/Migrations`) are applied explicitly, never automatically at startup, so they can be reviewed before deploy. `dotnet-ef` is pinned in `backend/.config/dotnet-tools.json`:

```bash
cd backend && dotnet tool restore
dotnet ef migrations add <Name> --project src/ChogGames.Infrastructure --startup-project src/ChogGames.Infrastructure --output-dir Persistence/Migrations
dotnet ef migrations script --idempotent --project src/ChogGames.Infrastructure --startup-project src/ChogGames.Infrastructure -o migrations.sql
```

PostgreSQL integration tests (migrations on a fresh database, readiness when healthy) run only when `CHOG_TEST_POSTGRES` holds a connection string for a server whose user can create databases, e.g. the local Compose instance:

```bash
export CHOG_TEST_POSTGRES="Host=localhost;Port=5432;Database=choggames;Username=choggames;Password=<password>"
dotnet test backend/ChogGames.sln
```

Allowed project references (enforced by `ChogGames.Architecture.Tests`): `Core` → none; `Games.*` and `Infrastructure` → `Core`; `Api` → `Core`, `Infrastructure`, `Games.*`.

## Continuous integration

`.github/workflows/ci.yml` runs on every pull request and push to `develop` and `master` (and manually via *Run workflow*). All jobs always run; there are no path filters or Nx affected shortcuts, and no Nx Cloud token is needed.

| Job | Checks |
|---|---|
| Frontend (Nx) | `npm ci`, `npm run test:workspace`, `nx run-many -t lint,test,build` for all frontend projects |
| Backend (.NET) | `dotnet restore/build/test` (Release) against a PostgreSQL 18.6 service, incl. integration tests; no model changes without a migration; migrations apply to an empty database; `.trx` results uploaded as an artifact |
| Docker Compose | config validation and `npm run deps:verify` smoke test |

Node.js and the .NET SDK come from `.nvmrc` and `backend/global.json`; actions are pinned to commit SHAs. `tools/ci.test.mjs` guards these rules.

## Start here

1. Review `docs/requirements.md` and `docs/decisions.md`.
2. Give the coding agent the root `AGENTS.md` file.
3. Follow `docs/backlog.md` in story order.
4. Implement one story at a time and keep acceptance criteria/tests green.
