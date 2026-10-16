# Chog Game Arena — Implementation Backlog v1.1 (Nx workspace)

**Source of truth:** `requirements.md` (product behavior); `architecture.md` (technical boundaries); `AGENTS.md` (agent instructions).  
**Status:** Planned. All stories are initially `Todo`.  
**Execution:** Follow story IDs in ascending order; complete one story per agent session or PR.

## Global definition of done

- Story acceptance criteria are verified with automated tests where practical.
- Native .NET solution builds and tests pass; relevant Nx frontend build/lint/tests pass when touched. Nx must not mask native .NET failures.
- Security, validation, concurrency and failure behavior are considered for the changed boundary.
- Relevant docs, migration scripts, API contract and test fixtures are updated.
- No feature outside the story scope is silently implemented.
- Mark story `Done` only after tests and manual checks actually pass.

## Story workflow

1. Read `AGENTS.md`, relevant requirements and architecture sections.
2. Select the **first unblocked Todo story**; inspect dependencies before editing.
3. Produce a short implementation plan, list files to change, then implement only that story.
4. Run tests/build; report actual results, risks and decisions.
5. Update status in this file only when acceptance criteria are met.

## Release gates

- **Local foundation:** E01–E02 complete.
- **NFT + multiplayer foundation:** E03–E05 complete.
- **First playable Chog Life:** E06 complete, local end-to-end test passing.
- **Public beta:** E07 complete and security/restore checks passed.
- **Second game:** E08 complete, shared services regression passed.
- **Later enhancements:** E09, not a launch blocker.

## Ordered epics, features, and stories


## E01 — Local foundation


### Feature: Repository & tooling


#### STORY-001 — Create monorepo skeleton

- **Status:** Done
- **Priority:** P1
- **Epic:** E01
- **Implementation:** Initialize a pinned Nx workspace at repository root without overwriting existing planning docs. Create `apps/`, `libs/`, `backend/`, `infrastructure/` and `docs/`; add `nx.json`, root `package.json` and committed lockfile. Configure local Nx caching; do not enable Nx Cloud. Add README with prerequisites and exact local commands. Do not scaffold the Angular app or .NET projects yet (STORY-002/003).
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** a fresh checkout has expected directories and existing docs preserved; `npm ci` and `npx nx --version` succeed using pinned compatible Node.js LTS/Nx versions; README identifies prerequisites and commands; no cloud account is required.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-002 — Initialize backend solution

- **Status:** Done
- **Priority:** P1
- **Epic:** E01
- **Implementation:** Create `backend/ChogGames.sln` and .NET 8 ASP.NET Core API, Core, Games and Infrastructure projects with dependency direction enforced. Add an explicit Nx backend task wrapper only if it adds value; native .NET commands remain authoritative.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** `dotnet build backend/ChogGames.sln` passes; API references Core; Core does not reference Infrastructure or API; if an Nx wrapper is configured it invokes the same build successfully.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-003 — Initialize Angular client

- **Status:** Done
- **Priority:** P1
- **Epic:** E01
- **Implementation:** Use Nx `@nx/angular` to generate Angular SPA under `apps/web`, with routing and shared layout. Create browser-side libraries only when needed; keep Angular/Nx versions compatible and pinned.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** `npx nx build web` passes; `/` and `/games` routes render; Nx recognizes `web` as a project; native Angular tooling remains accessible through Nx.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-004 — Docker Compose dependencies

- **Status:** Done
- **Priority:** P1
- **Epic:** E01
- **Implementation:** Provision PostgreSQL and Redis with named volumes and local-only ports.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** docker compose up -d starts healthy services; restart retains PostgreSQL data.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-005 — Configuration and secrets

- **Status:** Done
- **Priority:** P1
- **Epic:** E01
- **Implementation:** Add typed options, example env file, validation, and ignored local secrets.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** missing required settings fail startup clearly; no secret appears in git.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-006 — Migrations and health checks

- **Status:** Done
- **Priority:** P1
- **Epic:** E01
- **Implementation:** Add EF Core migrations and liveness/readiness endpoints.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** migration applies to empty database; readiness fails when PostgreSQL unavailable; liveness stays independent.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-007 — CI baseline

- **Status:** Done
- **Priority:** P1
- **Epic:** E01
- **Implementation:** Add GitHub Actions CI using pinned Node.js LTS and .NET 8 SDK: `npm ci`, Nx frontend build/lint/tests, native .NET restore/build/tests and Compose configuration validation. Begin with full checks; optimize with Nx affected only after correct base/head and project graph coverage are verified.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** pull request checks run automatically and fail on broken Nx frontend or native .NET builds/tests; no Nx Cloud token is needed; CI does not skip backend changes.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-065 — Web dev server on port 4201

- **Status:** Done
- **Priority:** P2
- **Epic:** E01
- **Implementation:** Serve the web app in development on port 4201 instead of Angular's default 4200, which other local projects use. Set the port on the `web` serve target, keep the API on 5248 behind the `/api` proxy, and move the development wallet sign-in domain (`WalletAuth:Domain`/`Uri` in `appsettings.Development.json`) to `localhost:4201` so EIP-4361 messages match the page. Update README, CLAUDE.md and progress notes.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** `npm start` and `npm run start:all` serve the web app on `http://localhost:4201`, `/api` is proxied to the API on 5248, nothing of this project listens on 4200, and a development wallet challenge names `localhost:4201`.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story. No change to production configuration, the API port or CI.


## E02 — Suite and player identity


### Feature: Navigation & identity


#### STORY-008 — Game catalog

- **Status:** Done
- **Priority:** P1
- **Epic:** E02
- **Implementation:** Render Chog Life and Chog Race cards with Available, Coming Soon and Maintenance states from backend config.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** disabled game cannot be launched; status shown on desktop and mobile.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-009 — Guest identity

- **Status:** Done
- **Priority:** P1
- **Epic:** E02
- **Implementation:** Issue durable anonymous guest session with opaque server identity and display name.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** refreshing retains guest identity; users cannot impersonate another guest by supplying an ID.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-010 — Player profile

- **Status:** Done
- **Priority:** P1
- **Epic:** E02
- **Implementation:** Persist player display name, selected avatar and minimal games/wins counters.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** profile can be updated by owner only; invalid display names rejected.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-011 — Mobile-first shell

- **Status:** Done
- **Priority:** P1
- **Epic:** E02
- **Implementation:** Implement responsive navigation, loading/error/empty states and keyboard-accessible controls.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** core pages usable at 375px width; navigation works without mouse.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


## E03 — Wallet and NFT collection


### Feature: Wallet authentication


#### STORY-012 — Wallet connect UX

- **Status:** Done
- **Priority:** P1
- **Epic:** E03
- **Implementation:** Connect Monad-compatible wallet and show chain/address with disconnect option.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** unsupported chain prompts switch; disconnect clears authenticated wallet session.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-013 — Challenge-sign authentication

- **Status:** Done
- **Priority:** P1
- **Epic:** E03
- **Implementation:** Backend issues nonce challenge bound to domain, chain, address and expiry; verify signature; rotate nonce.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** replayed/expired/wrong-domain signatures fail; no wallet authentication by address alone.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-014 — Mock NFT provider

- **Status:** Done
- **Priority:** P1
- **Epic:** E03
- **Implementation:** Implement INftProvider with deterministic local fixtures for zero, one and multiple NFTs.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** local development works without RPC; fixtures cover missing images and unknown traits.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-015 — Monad NFT ownership provider

- **Status:** Done
- **Priority:** P1
- **Epic:** E03
- **Implementation:** Configure official contract and RPC/indexer adapter; validate ownership server-side.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** wallet with 0/1/many NFTs returns correct eligible token IDs; RPC failure returns controlled error.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-016 — NFT metadata normalization and cache

- **Status:** Done
- **Priority:** P1
- **Epic:** E03
- **Implementation:** Persist token ID, collection, image URI, trait type/value, owner and refresh timestamp.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** traits survive unknown values; refresh updates stale owner; bad metadata does not crash request.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-017 — My Chogs gallery

- **Status:** Done
- **Priority:** P1
- **Epic:** E03
- **Implementation:** Display all verified NFTs, details and a standard Chog fallback.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** multiple NFTs shown; no NFT user can still select a standard character.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-018 — Character eligibility snapshot

- **Status:** Done
- **Priority:** P1
- **Epic:** E03
- **Implementation:** Verify selected NFT ownership before match start; snapshot character metadata and prevent mid-match swap.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** unowned token rejected; transfer mid-match does not alter active snapshot; next match revalidates.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


## E04 — Shared multiplayer platform


### Feature: Room lifecycle


#### STORY-019 — Game registry and room model

- **Status:** Done
- **Priority:** P1
- **Epic:** E04
- **Implementation:** Persist supported game definitions, private room, host, status and six-player capacity.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** unknown/disabled game rejected; room codes unique; host assigned.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-020 — Create/join room API

- **Status:** Done
- **Priority:** P1
- **Epic:** E04
- **Implementation:** Create room and join by code or link with membership validation.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** second player joins; invalid/full/started room rejected with stable error codes.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-021 — Lobby and ready state

- **Status:** Done
- **Priority:** P1
- **Epic:** E04
- **Implementation:** Display participants, chosen Chogs, abilities, host and ready status.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** host can start only with 2-6 valid ready players; joining player sees same lobby.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-063 — Leave a waiting room

- **Status:** Done
- **Priority:** P1
- **Epic:** E04
- **Implementation:** Let a member leave a `Waiting` room (DEC-023). A member who leaves is removed, which frees their seat and their one open room (DEC-021); nobody else's ready status changes. When the host leaves, the room becomes `Cancelled` for everyone: members are free to create or join other rooms, and the code no longer accepts joins (`room_closed`). The room's row is locked and then the player's, as for joining. Web: a Leave room button in the lobby; the host is warned first that leaving closes the room for everyone. Implement after STORY-021, before STORY-022. Leaving an `Active` match is out of scope (STORY-023/025/027).
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** a member who leaves is gone from the lobby and can join or create another room; when the host leaves, the room is cancelled and its other members can join or create other rooms.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** a non-member, an unknown code or a room that is not waiting is refused without changing the room or its members.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story. No host transfer, kicking or leaving an active match.


#### STORY-022 — SignalR auth and groups

- **Status:** Done
- **Priority:** P1
- **Epic:** E04
- **Implementation:** Authenticate hub connections and authorize room membership before subscription.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** non-member cannot subscribe or send commands; group messages only reach members.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-023 — Authoritative match state machine

- **Status:** Done
- **Priority:** P1
- **Epic:** E04
- **Implementation:** Model Waiting, Active, Completed, Cancelled and allowed transitions.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** illegal transitions rejected; state changes tested without frontend.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-024 — Command envelope and idempotency

- **Status:** Done
- **Priority:** P1
- **Epic:** E04
- **Implementation:** Define actionId, matchId, expectedVersion, payload; reject duplicates/stale commands.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** retry of same action changes state once; concurrent stale requests cannot both succeed.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-025 — Turn and AFK timer

- **Status:** Done
- **Priority:** P1
- **Epic:** E04
- **Implementation:** Server controls current player, configurable turn timeout and deterministic safe fallback.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** wrong player action rejected; expired turn advances exactly once.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-026 — Realtime state synchronization

- **Status:** Done
- **Priority:** P1
- **Epic:** E04
- **Implementation:** Publish versioned snapshots/events and request fresh snapshot on gap.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** two clients converge on same state after every command; missed version triggers resync.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-027 — Disconnect and reconnect

- **Status:** Done
- **Priority:** P1
- **Epic:** E04
- **Implementation:** Restore identity, membership, selected character and current authoritative state.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** browser refresh/reconnect returns active match; disconnected player cannot gain extra turn.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-028 — Match persistence and result records

- **Status:** Done
- **Priority:** P1
- **Epic:** E04
- **Implementation:** Persist snapshots/checkpoints and final results with player stats updates.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** backend restart restores or safely terminates match per documented policy; winner stored once.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-029 — Randomness and security tests

- **Status:** Done
- **Priority:** P1
- **Epic:** E04
- **Implementation:** Generate dice/cards/events only on server using secure RNG and reject forged payloads.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** client-supplied roll ignored; tests cover unauthorized room/turn/ability commands.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


## E05 — Reusable ability engine


### Feature: Ability abstraction


#### STORY-030 — Define ability catalog and execution contract

- **Status:** Done
- **Priority:** P1
- **Epic:** E05
- **Implementation:** Introduce game-specific ability definitions with eligibility, timing, target and parameter validation.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** core engine invokes abilities by ID without NFT trait knowledge.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-031 — Shared ability pool strategy

- **Status:** Done
- **Priority:** P1
- **Epic:** E05
- **Implementation:** Implement IAbilityAssignmentStrategy with SharedAbilityPool default.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** guest and NFT holder see identical game-specific abilities; traits do not change pool.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-032 — Trait-ready metadata boundary

- **Status:** Done
- **Priority:** P1
- **Epic:** E05
- **Implementation:** Define future TraitBased strategy interface and normalized traits; do not implement resolver.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** no game module directly reads NFT traits; swapping resolver requires no engine changes.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-033 — Lobby ability selection and lock

- **Status:** Done
- **Priority:** P1
- **Epic:** E05
- **Implementation:** Require one valid ability per player and persist configured version/uses at start.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** cannot ready without ability; cannot switch after start; unknown ability rejected.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-034 — Ability execution and usage tracking

- **Status:** Done
- **Priority:** P1
- **Epic:** E05
- **Implementation:** Validate timing/target, execute once, persist usage and broadcast result.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** duplicate use blocked; invalid target has no state change; concurrent use resolves once.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


## E06 — Chog Life playable vertical slice


### Feature: Board and economy


#### STORY-035 — Life rules configuration

- **Status:** Done
- **Priority:** P1
- **Epic:** E06
- **Implementation:** Define versioned board of 30-40 spaces, starting 100 fictional MON, 20-minute cap and finish bonus.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** config validates tile references and values; no real token transfers occur.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-036 — Board rendering

- **Status:** Done
- **Priority:** P1
- **Epic:** E06
- **Implementation:** Render player positions, current turn and tile type on responsive board.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** 2-6 pawns visible; board remains usable on mobile.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-037 — Roll and movement

- **Status:** Done
- **Priority:** P1
- **Epic:** E06
- **Implementation:** Server generates move and resolves position within valid paths.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** out-of-turn roll rejected; every client receives same destination.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-038 — Tile event resolver

- **Status:** Done
- **Priority:** P1
- **Epic:** E06
- **Implementation:** Implement reward, loss, market, airdrop, rug and liquidation events with configurable outcomes.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** each event has deterministic tests with injected RNG; balance never becomes invalid.
  - **Given** a live match, **when** a tile changes a player's balance, **then** every player sees each player's liquid MON next to their name in the board legend, updated in the same snapshot as the move.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-039 — Safe vs Degen branch

- **Status:** Done
- **Priority:** P1
- **Epic:** E06
- **Implementation:** Allow choice at designated tile with validated risk and guaranteed alternatives.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** only offered choices accepted; timeout applies configured safe default.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-040 — Portfolio and assets

- **Status:** Done
- **Priority:** P1
- **Epic:** E06
- **Implementation:** Track liquid MON, assets, bonuses and liabilities and compute total portfolio.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** valuation consistent across clients; zero/negative balances handled by explicit rules.
  - **Given** a live match, **when** any player's portfolio changes, **then** every player sees each player's liquid MON, assets, bonuses, liabilities and total portfolio in the board legend, matching the server values.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-041 — Finish and time cap

- **Status:** Done
- **Priority:** P1
- **Epic:** E06
- **Implementation:** Complete on configured finish condition or 20-minute deadline and rank by portfolio.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** tie-breaker deterministic; time cap yields one result even with unfinished players.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-042 — Life abilities

- **Status:** Done
- **Priority:** P1
- **Epic:** E06
- **Implementation:** Implement Reward Boost, Loss Protection, Event Preview, Lucky Reroll.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** each works once; correct timing enforced; preview does not change hidden RNG outcome.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-043 — Life result and statistics

- **Status:** Done
- **Priority:** P1
- **Epic:** E06
- **Implementation:** Show ranking, starting/final portfolio, rugs, liquidations, degen score and winner.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** server results match UI; match record survives reload.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-044 — Life balancing and multiplayer QA

- **Status:** Done
- **Priority:** P1
- **Epic:** E06
- **Implementation:** Run seeded simulation and real 2/4/6-player tests including AFK and reconnect. Done (DEC-047): `LifeSimulator` plays whole matches through the real engine, rules and abilities on seeded chance; `ChogLifeMultiplayerTests` plays 2/4/6-player matches through the API and hub. Results in `docs/games/chog-life-balancing.md`; rules version 2 halves the finish bonus to 50 MON, and the duration target is revised.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** no stuck turn in automated simulations; duration and outcome distribution documented.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-064 — Chog Life illustrated board

- **Status:** Done
- **Priority:** P2
- **Epic:** E06
- **Implementation:** Replace the functional vertical track of STORY-036 with an illustrated Chog Life board in the web app, once the rules are final (requirements §15, after STORY-044). Draw the route as a winding SVG path with each Safe/Degen fork as a visible split that joins again, tile icons and artwork by type and event kind, and pawns that animate from tile to tile along the route the server resolved (STORY-037). Lay the board out from the server's tile graph (`state.tiles`, DEC-038), never from a copy in the client, so a new rules version needs no web change. Keep the STORY-036 vertical track as the accessible fallback (screen readers, reduced motion) and wherever the illustrated board doesn't fit. No API, rules or server change. Implement after STORY-044.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** the illustrated board shows every tile, both lanes of each fork, 2–6 pawns and the current turn as the server sends them; it stays usable at 375px without horizontal page scroll; pawn moves animate along the resolved route and are skipped with reduced motion.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** a state the client can't read shows the fallback or no board, and the board never changes a position on its own.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story. No change to rules, odds, the board layout data or the API; no Chog Race board; no collector cosmetics (STORY-060).


## E07 — First-game launch and operations


### Feature: Staging & release


#### STORY-045 — Production-like Compose deployment

- **Status:** Done
- **Priority:** P1
- **Epic:** E07
- **Implementation:** Create API, PostgreSQL, Redis, reverse proxy deployment config with persistent volumes.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** deploy on single Linux VM; app restarts without losing database.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-046 — Frontend hosting and HTTPS

- **Status:** Done
- **Priority:** P1
- **Epic:** E07
- **Implementation:** Build static frontend for CDN or reverse proxy with HTTPS and WebSocket proxy.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** hTTPS works; WebSocket upgrade works; no mixed-content errors.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-047 — Backup and restore runbook

- **Status:** Done
- **Priority:** P1
- **Epic:** E07
- **Implementation:** Schedule encrypted PostgreSQL backups and document restore with retention.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** restore to clean environment tested; secrets excluded from backup archive.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-048 — Operational monitoring

- **Status:** Done
- **Priority:** P1
- **Epic:** E07
- **Implementation:** Add structured logs, request correlation, readiness and basic game/session metrics.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** health/metrics observable; wallet secrets and signatures never logged.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-049 — Release checklist and smoke tests

- **Status:** Done
- **Priority:** P1
- **Epic:** E07
- **Implementation:** Test guests, wallet, NFT selection, 2-6 players, reconnect, finish and rollback.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** documented checklist passes on staging before public launch.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


## E08 — Chog Race


### Feature: Race gameplay


#### STORY-050 — Race configuration and board

- **Status:** Done
- **Priority:** P1
- **Epic:** E08
- **Implementation:** Define 24-30 spaces, movement 1-3, max 20 minutes, win by reaching finish.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** config validation rejects unreachable finish; mobile board renders 6 pawns.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-051 — Race movement and turn phases

- **Status:** Done
- **Priority:** P1
- **Epic:** E08
- **Implementation:** Implement move, landing tile, optional card and end-turn phases.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** server rejects out-of-phase actions; moves broadcast identically.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-052 — Deck and hand management

- **Status:** Done
- **Priority:** P1
- **Epic:** E08
- **Implementation:** Define shuffled deck, discard, draw and max hand of 3.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** draw is server-side; hidden cards visible only to owner; exhaustion handled.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-053 — Movement and chaos cards

- **Status:** Done
- **Priority:** P1
- **Epic:** E08
- **Implementation:** Implement Pump, Whale, Airdrop, FOMO and Reverse using configured values.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** movement bounded by board; swap and reroll deterministic in tests.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-054 — Attack and defense cards

- **Status:** Done
- **Priority:** P1
- **Epic:** E08
- **Implementation:** Implement Rug, HODL, Liquidated and defense response window.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** invalid target rejected; HODL consumes once; AFK defense window expires safely.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-055 — Race abilities

- **Status:** Done
- **Priority:** P1
- **Epic:** E08
- **Implementation:** Implement Speed Boost, Shield, Card Vision and Card Steal.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** one selected ability per match; card privacy and hand capacity maintained.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-056 — Race finish and results

- **Status:** Todo
- **Priority:** P1
- **Epic:** E08
- **Implementation:** Declare first valid finisher winner; resolve simultaneous effects consistently.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** one winner; finish during chained effect resolves exactly once.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-057 — Cross-game regression

- **Status:** Todo
- **Priority:** P1
- **Epic:** E08
- **Implementation:** Verify same wallet, NFT, rooms, profiles and realtime infrastructure serve both games.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** no duplicated room/auth service; both games playable concurrently.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


## E09 — Post-MVP shared progression


### Feature: Progression


#### STORY-058 — Cross-game achievements

- **Status:** Todo
- **Priority:** P2
- **Epic:** E09
- **Implementation:** Award cosmetic achievements from verified match results.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** duplicate event does not award twice; progress shown on profile.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-059 — XP and leaderboards

- **Status:** Todo
- **Priority:** P2
- **Epic:** E09
- **Implementation:** Calculate configurable XP and per-game/season rankings.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** only completed valid matches count; ties deterministic.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-060 — Collector cosmetics

- **Status:** Todo
- **Priority:** P2
- **Epic:** E09
- **Implementation:** Add cosmetic-only rewards for NFT collection milestones.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** owning more NFTs does not alter available abilities or win probability.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-061 — Trait-based ability assignment future

- **Status:** Todo
- **Priority:** P2
- **Epic:** E09
- **Implementation:** Implement optional TraitBased resolver with fallback and multiple-trait rules.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** feature flag defaults off; tests verify nonholder parity and no engine refactor.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-062 — Uploaded profile avatar

- **Status:** Todo
- **Priority:** P2
- **Epic:** E09
- **Implementation:** Let a player upload a photo from their device as profile avatar. The server decodes and re-encodes every upload to a small square WebP (e.g. 256×256), dropping metadata such as EXIF/GPS, and stores only the re-encoded image in PostgreSQL. One uploaded avatar per player, replacing the previous one; the player can remove it to return to a built-in avatar. Images are served by the API with a fixed content type, `nosniff` and cache headers; original bytes are never served. Upload size cap and per-player rate limit. Independent of the match character (DEC-017). Blocked by DEC-018 (image library and moderation).
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** a JPEG/PNG/WebP upload becomes the player's avatar as a re-encoded square image without metadata; replacing or removing it works; other players see it wherever the avatar is shown.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** oversized, non-image, malformed or disguised files are refused without changing the avatar, and no player can change another player's avatar.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story. No new storage service or container; NFT avatars are out of scope.
