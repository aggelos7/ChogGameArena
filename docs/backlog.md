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
- **Third game:** E10 complete, shared services regression passed; starts after the public launch.

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


#### STORY-069 — Terms, privacy and photo reports

- **Status:** Done
- **Priority:** P1
- **Epic:** E07
- **Implementation:** What the public launch needs for uploaded photos (DEC-069, DEC-070). Terms of use and privacy pages in plain language, describing what the app stores (sessions, wallet address, photos, backups), linked from every page's footer, with the contact address from `Legal:ContactEmail`; the text is a draft for the product owner's legal review. Uploading a photo needs a one-time acceptance of the current terms version, stored with the player. A Report button on any other player's photo (profile, boards, Ladder) with a fixed reason (sexual content, hate or harassment, violence, scam or spam, other), one report per player per photo and a rate limit. A photo reported by 3 different players is hidden from everyone but its owner, who sees it marked under review. Operators are the wallets in `Moderation:OperatorWallets`; only they see a Reports page listing reported photos with counts and reasons, where they remove the photo or dismiss its reports.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** the terms and privacy pages are reachable from every page, an upload asks once for acceptance, a report is stored with its reason, the third distinct reporter hides the photo for everyone but its owner, and an operator's remove or dismiss clears it from the list.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: uploads without acceptance, reports of your own or a missing photo, unknown reasons, repeat reports and reports over the limit are refused, and anyone who isn't an operator gets `404` from the Reports page and its API.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story. Photos only: no reports of names or players, no bans, no automatic image scanning, no email notifications.


#### STORY-085 — Profile avatars in rooms and results

- **Status:** Done
- **Priority:** P1
- **Epic:** E07
- **Implementation:** Found in the launch's local release test: the room's player list and both games' results draw a badge in the match character's colour (Classic, or the NFT fallback) with the character's initial, not the avatar the player chose on their profile, so a player's colour differs between the header and the room. Rooms send each member's profile avatar (`avatar { id, name, color, imageUrl }`, the photo following the reports' hiding rule of DEC-070), and the player list and results draw it with the player's initial and collector frame; the match character stays as text. No migration.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** the room's player list and the Chog Life and Chog Race results show each player's chosen avatar colour or photo, the same as the header, with the match character still named.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: a photo hidden by reports shows only to its owner, and a retired avatar id falls back to the default.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story. The boards' pawns are unchanged.


#### STORY-086 — PostgreSQL healthy only when really ready

- **Status:** Done
- **Priority:** P1
- **Epic:** E07
- **Implementation:** Found when CI on `master` failed `backup:verify` before the `v1.0.0` tag: on a new database, PostgreSQL's image first runs a temporary server that listens only on its Unix socket, then restarts. The healthcheck (`pg_isready` over the socket) passes during that temporary phase, so services and tests that wait for "healthy" can reach the database while it restarts. The healthchecks of the dev and production Compose files and CI's PostgreSQL service check over TCP (`-h 127.0.0.1`), which the temporary server doesn't listen on, so "healthy" means the real server is up. A workspace test keeps them on TCP.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** every PostgreSQL healthcheck (dev, production, CI) checks over TCP, and `npm run deps:verify`, `npm run deploy:verify` and `npm run backup:verify` pass.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: a healthcheck over the socket again fails the workspace tests.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Also:** CI on this story's PR then failed `backup:verify`'s damaged-archive check once: the test wrote a fixed `X` over byte 300 of the encrypted archive, which left it intact whenever that byte already was an `X` (1 run in 256). The test now writes a byte different from the one there and checks the archive changed.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story. Healthchecks and the smoke test only; no change to the database, its settings or the backup scripts.


#### STORY-087 — Serve the site over IPv6

- **Status:** Done
- **Priority:** P2
- **Epic:** E07
- **Implementation:** At the launch (DEC-073) nginx published ports 80 and 443 on IPv4 only (`HTTP_BIND=0.0.0.0`), so the domain's `AAAA` record pointed at an address where nothing answered and was removed. Publish nginx on IPv6 too, next to IPv4 (for example a second binding, or `HTTP_BIND_V6` defaulting to `::`), keeping `HTTP_BIND`'s existing meaning so the local release stack's `127.0.0.1` still works. `deploy:verify` checks that the stack answers over IPv6 where the machine has it. The runbook says when to add the `AAAA` record back. Let's Encrypt keeps validating over IPv4 or IPv6.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** the production stack answers HTTPS and the HTTP redirect on both IPv4 and IPv6, and the `AAAA` record can be restored.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: host names other than `PUBLIC_HOSTNAME` are still refused over IPv6, and a local stack bound to `127.0.0.1` publishes nothing on other addresses.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story. Port publishing and docs only; no change to TLS, nginx's server blocks or the app. *Widened by DEC-075:* IPv6 on the `backend` network and `listen [::]` beside each IPv4 listen, so IPv6 visitors keep their own address.


#### STORY-088 — Rename to Chog Kingdom at chogkingdom.xyz

- **Status:** Done
- **Priority:** P1
- **Epic:** E07
- **Implementation:** "Chog Arena" turned out to be taken, so the product is called **Chog Kingdom** and moves to `chogkingdom.xyz` (DEC-074). Rename everything players see (header, tab titles, the home page's hidden heading, the terms, the wallet sign-in statement), replace the logo with the product owner's "Chog Kingdom" artwork, and update the docs that name the production domain. The server move (`PUBLIC_HOSTNAME`, contact addresses, certificate, the old domain's redirect, monitors) follows `docs/deployment.md` with the release.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** every page, tab title, the terms and the wallet sign-in message say "Chog Kingdom", and the home page shows the new logo.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: no player-visible "Chog Arena" remains, and sign-in challenges issued before the change still verify.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story. Names and artwork only; no change to the repository, code or image names.


#### STORY-089 — Music and general fixes

- **Status:** Done
- **Priority:** P2
- **Epic:** E07
- **Implementation:** One story for the small changes of one session (DEC-076). **Music:** play the product owner's theme track (`public/audio/chog-kingdom-theme.mp3`) as the music of the whole site. Browsers only allow sound after an interaction, so it starts on the visitor's first click, tap or key press and then loops quietly (35% volume) across pages without restarting on navigation; nothing downloads before that. While the player is in a live match the theme pauses and the game's own track plays instead (a game without one is silent); the theme resumes where it was when the match ends or they leave. **Rolling die:** each roll in Chog Life and Chog Race shows a die tumbling over the board with a rolling sound, landing on the server's number before the pawn hops. **Car sounds:** in Chog Race an engine starts while a pawn drives on and a back-up beeper sounds while one goes back. **What each move did:** after its die and hops, each move shows a banner over the board, a popup over its tile and a line in a Recent moves list; the last-move line waits for the die too. **Last move:** a match's last move (the roll and hops onto the finish) is shown on the live board before the results appear. A speaker toggle in the header mutes and unmutes all music and sounds, remembered on the device. **General fixes:** the collector badge sits centred beside a name everywhere; what's yours (unlocked achievements, your Ladder row, your chosen Chog) has a purple background with white text, and other tiles on My Chogs and the Ladder have thin dashed borders like the profile's tiers, without the white card; the profile's wallet panel moves up beside your summary as a card, with the wallets as a row of logos and a one-line note.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** the theme starts on the first interaction and loops at 35% volume across pages, pauses for the game's track during a live match and resumes after it, a roll shows the die landing on its number with the rolling sound before the pawn hops, a Chog Race car sounds while it moves (reversing when it goes back), and the header toggle mutes all of it and plays it again; the profile shows the wallet card beside the summary.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: a remembered mute keeps every page silent, a refused playback is retried on the next interaction, and blocked storage only makes the choice last for the visit.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story. The changes listed here as the session adds them; no volume slider and no server change. Web only.

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

- **Status:** Done
- **Priority:** P1
- **Epic:** E08
- **Implementation:** Declare first valid finisher winner; resolve simultaneous effects consistently.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** one winner; finish during chained effect resolves exactly once.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-057 — Cross-game regression

- **Status:** Done
- **Priority:** P1
- **Epic:** E08
- **Implementation:** Verify same wallet, NFT, rooms, profiles and realtime infrastructure serve both games.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** no duplicated room/auth service; both games playable concurrently.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-066 — Chog Race Degen Score

- **Status:** Done
- **Priority:** P2
- **Epic:** E08
- **Implementation:** Add a Degen Score to Chog Race results, computed by the server from the attacks the player landed (STORY-056's `attacksLanded`: Rug, Liquidated or Whale not blocked, and Reverse that pushed someone back). Proposed formula, version 1: 10 points per attack landed, at most 100. Like Chog Life's (DEC-046), it is display only and never changes the ranking, and it is stored in the result with its formula version (`degenScore`, `degenScoreVersion`). The Results panel shows it as a tile ("Degen Score n/100") with a note that it doesn't count towards the ranking. Points and cap confirmed by the product owner (DEC-062).
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** each player still playing gets a Degen Score from their attacks landed, capped at 100 and stored with its formula version; the ranking is unchanged; every player sees the same scores live and after a reload.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: blocked attacks, leavers and abandoned matches earn nothing, and no client can set a score.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story. No change to Chog Life's Degen Score, no lifetime totals or rewards, no migration.


#### STORY-067 — Chog Race board layout, arrows, cars and hops

- **Status:** Done
- **Priority:** P2
- **Epic:** E08
- **Implementation:** Refine the Chog Race board's look and feel, as the product owner asked (DEC-067). From 1200px wide the board is laid out like Chog Life's (DEC-048): the title, players, clock, turn and cards in a 340px column on the left, the track on the right filling the height left on screen, with the columns that give the largest tiles. An arrow image in each gap leads from tile to tile along the winding track (none from the Finish). Pawns are cartoon cars in the seat colour, facing the way their row runs, with the player's photo or seat number on the roof. Each new move is shown a tile at a time, as on the Chog Life board: the roll, on through a Boost or Setback, a card's move, then pawns pushed back by Rug or Reverse; never for the first board or with reduced motion.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** the wide layout puts the players and controls beside a track that fills the height left on screen; every tile but the Finish has an arrow to the next; pawns are drawn as cars with the driver on the roof; a new move hops a tile at a time while screen readers hear where the server put each pawn; phones keep the stacked layout with three pawns' worth of room in the narrowest tile.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: the board only draws what the server sent, and hops never change a pawn's position.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story. Web only: no API, rules, config or migration change, and no change to the Chog Life board beyond exporting its reduced-motion check.


#### STORY-068 — Home with games, backgrounds and header refinements

- **Status:** Done
- **Priority:** P2
- **Epic:** E08
- **Implementation:** Refine the suite's look and feel, as the product owner asked. The home page is the game catalogue: `/games` redirects to `/`, the Games tab is gone, and the page shows the arena logo over a blurred, full-width copy of it, fitting the screen without scrolling (phones included), with each game's picture behind its card. Chog Royale is listed as Coming Soon (configuration only, no rules yet). The header is the logo's night blue with white text and a gold accent, shows the player's avatar and name as the link to their profile (no Profile tab), and draws a thin line under a hovered tab. The Leaderboard is called the Ladder (`/ladder`; `/leaderboard` redirects). Chog Race and Chog Life matches are played over their own pictures, with clear, blurred side panels; every other page sits over the home page's arena, drawn the same way and with the logo veiled, with white text outside light boxes. Chog Life tiles are drawn 1.3 times larger, and on wide screens each fork's lanes are folded in two (DEC-068). Chog Life results show each player's total MON prominently with its gain or loss, and the race board's rows keep one height.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** `/` lists the games, Chog Royale as Coming Soon, and `/games` and `/leaderboard` redirect; the header links the player's avatar and name to their profile and follows avatar and photo changes; the folded Chog Life board keeps every tile apart at every column count; the results show each total with its change.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: unknown or unavailable games still redirect to the catalogue, and the header shows the name alone when the profile can't be loaded.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story. No API, rules or migration change; the only configuration change is the Chog Royale catalogue entry.

## E09 — Post-MVP shared progression


### Feature: Progression


#### STORY-058 — Cross-game achievements

- **Status:** Done
- **Priority:** P2
- **Epic:** E09
- **Implementation:** Award cosmetic achievements from verified match results. Catalog, history backfill and display confirmed by the product owner (DEC-063).
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** duplicate event does not award twice; progress shown on profile.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-059 — XP and leaderboards

- **Status:** Done
- **Priority:** P2
- **Epic:** E09
- **Implementation:** Calculate configurable XP and per-game/season rankings. XP rules, boards, seasons, ties and display confirmed by the product owner (DEC-064).
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** only completed valid matches count; ties deterministic.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-060 — Collector cosmetics

- **Status:** Done
- **Priority:** P2
- **Epic:** E09
- **Implementation:** Add cosmetic-only rewards for NFT collection milestones.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** owning more NFTs does not alter available abilities or win probability.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-061 — Trait-based ability assignment future

- **Status:** Done
- **Priority:** P2
- **Epic:** E09
- **Implementation:** Implement optional TraitBased resolver with fallback and multiple-trait rules.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** feature flag defaults off; tests verify nonholder parity and no engine refactor.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-062 — Uploaded profile avatar

- **Status:** Done
- **Priority:** P2
- **Epic:** E09
- **Implementation:** Let a player upload a photo from their device as profile avatar. The server decodes and re-encodes every upload to a small square WebP (e.g. 256×256), dropping metadata such as EXIF/GPS, and stores only the re-encoded image in PostgreSQL. One uploaded avatar per player, replacing the previous one; the player can remove it to return to a built-in avatar. Images are served by the API with a fixed content type, `nosniff` and cache headers; original bytes are never served. Upload size cap and per-player rate limit. Independent of the match character (DEC-017). Library, limits and moderation confirmed by the product owner (DEC-018).
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** a JPEG/PNG/WebP upload becomes the player's avatar as a re-encoded square image without metadata; replacing or removing it works; other players see it wherever the avatar is shown.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** oversized, non-image, malformed or disguised files are refused without changing the avatar, and no player can change another player's avatar.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story. No new storage service or container; NFT avatars are out of scope.


### Feature: Holder advantage


#### STORY-090 — Holder ability pool and Tier strength

- **Status:** Done
- **Priority:** P1
- **Epic:** E09
- **Implementation:** Give Chog holders an advantage in matches (DEC-077). A holder is a player whose match character is a Chog they own, checked live at start; the Tier comes from that Chog's `Tier` trait. A new holder strategy replaces `SharedAbilityPoolStrategy` as the registered `IAbilityAssignmentStrategy`: everyone may choose the game's 2 standard abilities, holders all of them. It returns each ability with the values of the character's Tier, and `AbilityPool`/`AbilitySnapshot` lock those values at start instead of requiring the catalog's own (catalog v2 for both games). The split and the Tier table live in `Abilities:Holder`, checked at startup against each handler's parameter ranges. The existing abilities get the new values they need: Chog Life Event Preview `uses`, Lucky Reroll `draws`; Chog Race Shield `blocks`, Card Vision `cards`, Card Steal `cards`, with their effects scaled. Chog Life: Reward Boost and Loss Protection standard, Event Preview and Lucky Reroll holder-only. Chog Race: Shield and Card Vision standard, Speed Boost and Card Steal holder-only. The match's ability panel shows the locked values and uses left. Update NFT-011–017 and the "Equal abilities" acceptance test in `docs/requirements.md`.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** a guest or a standard Chog sees only the 2 standard abilities with Base values, a holder of a Common Chog sees all of the game's abilities with Base values, and holders of rarer Tiers lock the Tier's values from the config, which their effects apply in the match (e.g. an Epic Speed Boost moves 4 more tiles, a Rare Shield blocks 2 attacks).
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: a non-holder choosing a holder-only ability is refused (`ability_unavailable`), a Chog sold before the start plays with the standard pool, traits and values never come from a request, a config change never alters a running match, and a bad `Abilities:Holder` value stops the API at startup.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story. No new abilities (STORY-091, STORY-092) and no Chog Royale abilities; no perk shown on My Chogs or the character picker.


#### STORY-091 — Chog Life holder abilities: Double Dice and Tax the Rich

- **Status:** Done
- **Priority:** P1
- **Epic:** E09
- **Implementation:** Two new holder-only Chog Life abilities (DEC-077), added to the catalog with their Tier values in `Abilities:Holder`. **Double Dice:** used before the roll; the server rolls `dice` dice (2, or 3 from Rare) and the player keeps one of them for that move; a turn that runs out keeps the highest. **Tax the Rich:** used on the player's turn; takes `percent` (10% to 20% by Tier) of the MON of the richest other player still in the match, capped, and adds it to the player's; ties pick the first by seat; refused when the player is already the richest. Both show in the match's ability panel and last-move line like the other abilities.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** a holder can lock and use either ability, Double Dice moves by the die they keep and Tax the Rich moves the Tier's share of MON from the richest opponent, with the results counted in the final standings.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: non-holders can't choose them, use outside the window, a second use or Tax the Rich while richest is refused with a stable code, and the kept die must be one the server rolled.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story. Chog Life only.


#### STORY-092 — Chog Race holder abilities: Double Dice and Freeze

- **Status:** Done
- **Priority:** P1
- **Epic:** E09
- **Implementation:** Two new holder-only Chog Race abilities (DEC-077), added to the catalog with their Tier values in `Abilities:Holder`. **Double Dice:** used before the roll; the server rolls `dice` dice (2, or 3 from Rare) and the player keeps one of them for that move; a turn that runs out keeps the highest. **Freeze:** used on the player's turn on an opponent still racing; that opponent skips their next turn, using the engine's existing skip-turn support (DEC-058), with `uses` 1, or 2 from Epic. Both show in the "Your ability" panel and the latest ability line like the other abilities.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** a holder can lock and use either ability, Double Dice moves by the die they keep and a Frozen opponent is passed over once, shown with a "Skips a turn" tag.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: non-holders can't choose them, use outside the window, beyond the Tier's uses, on a finished or absent opponent, or on one already skipping is refused with a stable code, and the kept die must be one the server rolled.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story. Chog Race only.


## E10 — Chog Royale

Requirements: [`docs/games/chog-royale-requirements.md`](games/chog-royale-requirements.md) (ROYALE-001–066). These stories replace the `ROY-STORY-001–022` drafts in [`docs/games/chog-royale-backlog.md`](games/chog-royale-backlog.md), which keeps the mapping. Chog Royale starts after the public launch (E07 release checklist passed). Its product questions are asked story by story, before coding, and recorded as decisions.


### Feature: Royale rounds and rules


#### STORY-070 — Simultaneous rounds in the shared engine

- **Status:** Done
- **Priority:** P1
- **Epic:** E10
- **Implementation:** Add a round mode to the shared match engine next to turns, chosen by a game's rules: every player still playing may act in the same round until its deadline; a player's submission is private, can be revised, and can be locked; the round resolves once, at the deadline or as soon as every player has locked, with the rules' default for anyone who didn't submit. Submissions must not make other players' commands stale; the room shows only who has submitted. Timers, missed rounds and the time cap run on the server whether clients are connected or not (ROYALE-004–008, 026, 031, 052, 061). Chog Life and Chog Race keep their turns unchanged.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** two to six players submit in the same round without `not_your_turn` or stale-version refusals, a round resolves exactly once at its deadline or when all have locked, and other players see only that someone submitted.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: outsiders, late, replayed and locked-round submissions are refused, and a disconnected player never holds up the round.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded, including the existing Chog Life and Chog Race engine and concurrency tests.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story. Engine only: no Chog Royale rules, cards or web screen; a test game's rules exercise the round mode.


#### STORY-071 — Royale configuration, match state and rounds

- **Status:** Done
- **Priority:** P1
- **Epic:** E10
- **Implementation:** Add the Chog Royale game module with versioned rules checked at API startup: 2–6 players, five rounds, round phases (event announcement, decisions, reveal) with their durations, 100 fictional MON each, integer balances that never go below zero, and the match's time cap (ROYALE-001–004, 010–012, 053–056, 059). A match records its rules version. Every action is HODL for now, so a match plays its five rounds to the end. Catalogue status comes from `CHOG_ROYALE_STATUS` (default `ComingSoon`). The lobby, Chog choice and ability choice are the suite's own.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** a Chog Royale match starts with 100 MON per player in round 1 and reaches its end after exactly five rounds without client input.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: invalid rules stop the API at startup, and a room of one cannot start.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story. No cards, events, rankings or game screen yet.


#### STORY-072 — Hidden actions, HODL and timeouts

- **Status:** Done
- **Priority:** P1
- **Epic:** E10
- **Implementation:** Add the round's actions: play a card from your hand (with a target for targeted cards), or HODL, revisable until you lock or the deadline passes; a player without an action at the deadline HODLs (ROYALE-005–008, 016–018, 048, 057, 060–061). HODL plays no card, uses no ability and keeps the hand. Each player's view shows their own pending action; others see only who has submitted until the reveal.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** only the last valid submission counts, an idle player HODLs at the deadline, and no other player's view or live update reveals an action before the reveal.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: a card not in the hand, a missing, own or departed target, and a late or duplicate action are refused.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story. Played cards have no effect yet.


#### STORY-073 — Royale deck and hands

- **Status:** Done
- **Priority:** P1
- **Epic:** E10
- **Implementation:** A versioned, weighted deck in the rules; each player is dealt three cards with match-unique card ids from server randomness, a played card is replaced after the round except after round five, and HODL keeps the hand (ROYALE-015–020, 062).
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** each player holds three private cards, a played card is replaced before the next round, and the same seeded randomness deals the same hands.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: no view or live update shows another player's hand.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-074 — Round resolution and ledger

- **Status:** Done
- **Priority:** P1
- **Epic:** E10
- **Implementation:** Resolve a locked round in the documented phase order (snapshot, Chaos, defenses, gains, Rugs, swaps, market effects, clamp), with seat priority rotated each round, against a snapshot taken before any effect (ROYALE-026–032). Each round adds a ledger entry per player (card, target, defense, market effect, change, resulting balance) to the match state and the reveal. Card effects are placeholders until STORY-075.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** the same state, actions and randomness always give the same balances and ledger, and seat priority rotates each round.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: a retried or repeated resolution never changes balances twice.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-075 — Pump, Rug Pull and Diamond Hands

- **Status:** Done
- **Priority:** P1
- **Epic:** E10
- **Implementation:** Pump gains 20 MON; Rug Pull steals up to 15 MON from a target, never more than they hold; Diamond Hands blocks the first hostile effect aimed at its player in resolution order and gains 5 MON (ROYALE-017, 025, 029). Values live in the rules version.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** a Pump gains 20, a Rug on a player holding 8 takes 8, and a shielded player hit twice loses only to the second attack.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: no balance goes below zero.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-076 — Whale Swap, Airdrop and Chaos

- **Status:** Done
- **Priority:** P1
- **Epic:** E10
- **Implementation:** Whale Swap exchanges 20% of balance with a target, at most 20 MON each way, from the pre-round snapshot, with at most one successful swap per player per round; Airdrop gains 5–25 MON from server randomness, recorded in the ledger; Chaos adds one modifier from a bounded pool, at most one per round however many are played (ROYALE-024–025, 030, 062). The Whale Swap edge cases are settled as a decision before coding.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** overlapping swaps leave each player in at most one, every Airdrop is 5–25 and logged, and several Chaos cards add one modifier.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: a swap with a shielded target is blocked and no balance goes below zero.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-077 — Market events

- **Status:** Done
- **Priority:** P1
- **Epic:** E10
- **Implementation:** Draw one shared, weighted market event per round from server randomness and show it before decisions: Meme Hype (Pump +50%), Market Crash (Pump halved), Whale Incoming (+5 to a successful Rug, within the victim's balance), Community Airdrop (+5 to everyone after cards) and Calm Market (ROYALE-021–025). Amounts round down to whole MON.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** every player sees the same event before deciding, and a Pump pays 30 under Meme Hype and 10 under Market Crash.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: no event is drawn or changed after decisions open.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-078 — Royale finish, rankings and stats

- **Status:** Done
- **Priority:** P1
- **Epic:** E10
- **Implementation:** After round five, rank by MON, then fewer losses taken, then more successful cards, then a shared place (ROYALE-009). Results carry each player's MON, change and tie-break values; profiles, XP and the Ladder count the match as decided before coding.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** standings follow the tie-breaks in order, exact ties share a place, and the profile counts the game.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: actions after the end are refused.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-079 — Royale simulations and balancing

- **Status:** Todo
- **Priority:** P1
- **Epic:** E10
- **Implementation:** Seeded tests that play many full two- and six-player matches with legal random actions, checking no balance goes negative and no round stalls, and reporting balance spreads, card win rates and match length (ROYALE-063, 065). Proposed tuning is a decision and a new rules version, never a silent change, like `docs/games/chog-life-balancing.md`.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** simulated matches always finish in five rounds with non-negative balances, and the same seeds give the same histories and rankings.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: simulations use the real command path and refuse nothing a player couldn't send.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-080 — Royale abilities

- **Status:** Todo
- **Priority:** P1
- **Epic:** E10
- **Implementation:** A Chog Royale ability catalog for the shared ability pool, once per match each, with timing windows (ROYALE-039–044): `diamond_hands_plus`, `alpha_leak` (next round's event shown only to its player), `double_pump`, and `last_second_exit` or its replacement, since actions are already revisable until the deadline. Guests and holders get the same choices; NFTs and traits give no advantage (ROYALE-037, 066).
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** each ability has its tested effect, `alpha_leak`'s preview reaches only its player, and guests and holders are offered the same abilities.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: a used ability or one outside its window is refused.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


### Feature: Royale client and release


#### STORY-081 — Royale game screen

- **Status:** Todo
- **Priority:** P1
- **Epic:** E10
- **Implementation:** The Chog Royale board in the room page, live through the room hub: market event, your three cards, opponents with balances and submitted marks, round counter and countdown; choosing a card, a target (only valid ones), HODL, lock and an ability (ROYALE-045–048, 051). Fits a 375px phone without sideways scrolling; keyboard usable; server errors shown without optimistic balance changes.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** a player can play a full match from the screen on a phone and a desktop, and sees others only as submitted.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: refused commands show the server's error and nothing changes.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-082 — Reveal, round history and results

- **Status:** Todo
- **Priority:** P1
- **Epic:** E10
- **Implementation:** Each round's reveal shows who played what at whom, blocks and swaps, the market effect and each player's MON change, within its time budget and shortened under reduced motion; the results show standings with ties and a readable round-by-round history (ROYALE-049, 051).
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** every player sees the same reveal and results as the server's ledger and standings.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: the reveal shows nothing the server hasn't revealed.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-083 — Royale reconnect and AFK

- **Status:** Todo
- **Priority:** P1
- **Epic:** E10
- **Implementation:** A reconnecting player gets the current round, phase, remaining server deadline, their own pending action and the public state, never others' hands or actions (ROYALE-050, 052). AFK players HODL each round under the missed-round rule decided in STORY-070.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** a player who drops mid-decision returns to their pending action and the real remaining time, and one who returns after the deadline sees the round's result.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: outsiders get no match state.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.


#### STORY-084 — Royale cross-game regression and release

- **Status:** Todo
- **Priority:** P1
- **Epic:** E10
- **Implementation:** Like STORY-057: a Chog Royale match played beside Chog Life and Chog Race at once with each room isolated, cross-game commands refused, and guest and holder parity (ROYALE-064, 066); `release:verify` plays a two-player Chog Royale match; the release checklist gains a Chog Royale section; matches are timed against the five-minute target (ROYALE-003, 065). `CHOG_ROYALE_STATUS` is `Available` on staging and in `deploy:verify`. No new production service.
- **Acceptance criteria:**
  - **Given** the prerequisite feature and valid test data, **when** this story's operation is performed, **then** all three games play at once on one stack, two- and six-player Chog Royale matches finish within five minutes, and `release:verify` passes with Chog Royale.
  - **Given** invalid or unauthorized input, **when** the operation is attempted, **then** it fails safely without an unauthorized state change: another game's commands, match ids and rooms are refused, and turning Chog Royale off leaves the other games playable.
  - **Given** the story's tests and build checks, **when** they are executed, **then** they pass and results are recorded.
- **Scope guard:** Do not implement later stories or future feature flags as part of this story.
