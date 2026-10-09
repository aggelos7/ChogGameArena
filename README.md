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
