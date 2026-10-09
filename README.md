# Chog Game Arena

Mobile-first multiplayer browser games for the Chog community on Monad. This repository contains the planning pack (requirements, ordered backlog of 61 stories, architecture, decision log and agent instructions) and the Nx workspace skeleton the application is built in.

## Repository tooling

Nx is the frontend monorepo/task orchestration tool. Angular lives in `apps/web`; shared TypeScript libraries live in `libs/`; the .NET 8 solution lives in `backend/` and remains buildable with native `dotnet` commands. Nx Cloud is not used (`neverConnectToCloud` in `nx.json`); task results are cached locally in `.nx/cache`. Docker Compose remains the local and VM deployment model.

```text
apps/web/        Angular 22 SPA (standalone, zoneless, esbuild, Vitest)
libs/            Shared browser-side TypeScript libraries
backend/         Native .NET 8 solution (ChogGames.sln: Api, Core, Games.*, Infrastructure)
infrastructure/  Docker Compose and deployment assets (STORY-004)
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
| Docker with Compose | v2 | required from STORY-004 |

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
npm start               # dev server on http://localhost:4200 (same as npx nx serve web)
npx nx build web        # production build to dist/apps/web
npx nx test web         # unit tests (Vitest via @angular/build:unit-test)
npx nx lint web         # ESLint (angular-eslint)
npx nx g @schematics/angular:component <path> --project=web   # Angular schematics via Nx
```

Routes: `/` (home), `/games` (games page); unknown URLs redirect to `/`.

### Backend

Native `dotnet` commands are authoritative; the SDK is pinned in `backend/global.json` and package versions in `backend/Directory.Packages.props`.

```bash
dotnet build backend/ChogGames.sln   # build all backend projects
dotnet test backend/ChogGames.sln    # run backend tests, incl. architecture dependency rules
npx nx run backend:test              # same commands via Nx (uncached wrapper)
```

Allowed project references (enforced by `ChogGames.Architecture.Tests`): `Core` → none; `Games.*` and `Infrastructure` → `Core`; `Api` → `Core`, `Infrastructure`, `Games.*`.

## Start here

1. Review `docs/requirements.md` and `docs/decisions.md`.
2. Give the coding agent the root `AGENTS.md` file.
3. Follow `docs/backlog.md` in story order.
4. Implement one story at a time and keep acceptance criteria/tests green.
