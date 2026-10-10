# Chog Royale — Implementation Backlog v1.0

**Product:** Chog Games Suite · **Game:** Chog Royale — Pump or Rug  
**Source:** `docs/games/chog-royale-requirements.md` (ROYALE-001–066)  
**Status:** Planned; all stories initially `Todo`  
**Scope:** Additive third game only. Do not renumber, rewrite, or block ongoing Chog Life / Chog Race stories.

## Delivery conventions

- Implement **one story per AI-agent session/PR**. Read `AGENTS.md`, suite architecture, this backlog and the Royale requirements first.
- Use the existing Nx workspace, Angular app, .NET solution, SignalR rooms, PostgreSQL, identity/NFT services and Docker Compose. Adapt file paths to the actual repository; do not create duplicate shared infrastructure.
- **Prerequisites are capability-based:** shared services may already exist even if original suite story statuses are stale. Verify interfaces before implementation.
- Server owns game state, timers, card draws, RNG, ability execution, balances, and rankings. No real MON transfers or staking.
- All players choose simultaneously; never introduce per-player sequential turns.
- `Done` requires verified acceptance criteria, relevant automated tests, successful impacted builds and a concise PR/test report. Do not claim tests passed unless run.
- Preserve existing game APIs and behavior; run shared regression tests on integration stories.

## Definition of done

1. New or modified behavior is covered by unit/integration tests as appropriate, including invalid inputs, timeouts and duplicate requests.
2. `dotnet test` on affected projects and Nx checks on affected frontend projects pass; native .NET build remains authoritative.
3. Public/private state separation is enforced; unauthorized players cannot submit commands or inspect hidden actions.
4. Config, event contracts, persistence migrations and documentation are updated if affected.
5. Existing Life/Race tests continue passing where shared code is touched.

## Epic overview and release gates

| Epic | Goal | Stories | Gate |
|---|---|---|---|
| **ROY-E01** | Royale backend game loop | ROY-STORY-001–008 | Complete five-round headless match |
| **ROY-E02** | Cards, economy, NFTs and abilities | ROY-STORY-009–015 | Full rules + character parity |
| **ROY-E03** | Client, resilience and release | ROY-STORY-016–022 | Playable 2–6 player beta |

**Prerequisite:** existing suite multiplayer/rooms, game registration, guest identity, optional wallet/NFT verification and ability interfaces. If an interface is not ready, record it as a blocker and implement the relevant original suite story first; do not rebuild it in Royale.

---

## ROY-E01 — Royale backend game loop

### Feature ROY-F01 — Registration, state and rounds

#### ROY-STORY-001 — Register Royale as a third game
- **Status:** Todo · **Priority:** P1 · **Depends on:** suite game registration/router
- **Requirements:** ROYALE-053–056, 059
- **Implementation:** Add game ID, route placeholder, versioned config and backend game-module registration; do not modify Life/Race rules.
- **Acceptance criteria:**
  - **Given** the running suite, **when** available games are queried, **then** Royale is listed with its ID and 2–6 player limits.
  - **Given** an unsupported config version, **when** a match is requested, **then** creation fails with a clear error.
  - **Given** existing games, **when** suite tests run, **then** Life/Race registrations and routes are unchanged.

#### ROY-STORY-002 — Define immutable match and round state
- **Status:** Todo · **Priority:** P1 · **Depends on:** 001
- **Requirements:** ROYALE-001–003, 010–014, 026, 032
- **Implementation:** Model seats, five rounds, phase, deadlines, integer portfolios starting at 100, hands, hidden actions, defense flags, ability usage and state version.
- **Acceptance criteria:**
  - **Given** 2–6 eligible players, **when** a match starts, **then** each has 100 fictional MON and round 1 begins.
  - **Given** 1 or 7 players, **when** start is attempted, **then** validation rejects it.
  - **Given** a state transition, **when** applied, **then** previous state is unchanged and state version increments once.

#### ROY-STORY-003 — Implement round phase machine and deadlines
- **Status:** Todo · **Priority:** P1 · **Depends on:** 002
- **Requirements:** ROYALE-002–004, 007–008, 052
- **Implementation:** Event announcement ≤5s, decisions 15s, reveal ≤10s, transition ≤5s, five rounds; server clock controls phases and early-lock option.
- **Acceptance criteria:**
  - **Given** a started match, **when** clock advances, **then** it reaches results after exactly five rounds without client input.
  - **Given** all players locked, **when** early-resolution policy allows, **then** the round advances before the full decision deadline.
  - **Given** a disconnected player, **when** deadline expires, **then** round progresses without waiting for that client.

### Feature ROY-F02 — Hidden simultaneous actions

#### ROY-STORY-004 — Validate and submit private actions
- **Status:** Todo · **Priority:** P1 · **Depends on:** 002–003
- **Requirements:** ROYALE-005–006, 017–019, 031, 057, 060–061
- **Implementation:** Add `SUBMIT_ACTION` using match/round/action IDs and expected version; validate actor, hand ownership, targets and deadline; allow revisions before lock.
- **Acceptance criteria:**
  - **Given** a valid player's card, **when** submitted in decision phase, **then** only their own private snapshot contains the selection.
  - **Given** a revised action before deadline, **when** accepted, **then** only the last valid action is resolved.
  - **Given** a duplicate ID, stale version, invalid target or late action, **when** submitted, **then** it cannot change resolved balances or leak opponents' choices.

#### ROY-STORY-005 — Implement HODL and timeout fallback
- **Status:** Todo · **Priority:** P1 · **Depends on:** 003–004
- **Requirements:** ROYALE-008, 016, 052
- **Implementation:** Explicit and automatic HODL, zero reward, no card consumption, no ability use.
- **Acceptance criteria:**
  - **Given** no valid action at deadline, **when** the round locks, **then** the server records HODL deterministically.
  - **Given** explicit HODL, **when** resolved, **then** the hand and balance remain unchanged except shared market effects.
  - **Given** a disconnected player, **when** all other actions complete, **then** no deadlock occurs.

#### ROY-STORY-006 — Implement deterministic resolution pipeline
- **Status:** Todo · **Priority:** P1 · **Depends on:** 004–005
- **Requirements:** ROYALE-026–032
- **Implementation:** Define phase ordering, rotated seat priority, attack/shield ordering, swap conflict handling and immutable pre-resolution snapshots; use stub card effects initially.
- **Acceptance criteria:**
  - **Given** identical initial state, config and RNG inputs, **when** resolved twice, **then** results and emitted events are identical.
  - **Given** competing attacks/swaps, **when** resolved, **then** rotated seat order and one-successful-swap-per-player rules apply.
  - **Given** repeated resolve invocation, **when** replayed, **then** portfolios are not changed twice.

### Feature ROY-F03 — Persistence and match results

#### ROY-STORY-007 — Persist ledger, events and config snapshot
- **Status:** Todo · **Priority:** P1 · **Depends on:** 006
- **Requirements:** ROYALE-013–014, 032, 058–059
- **Implementation:** Save match config version, authoritative round ledger and versioned events atomically using existing persistence patterns.
- **Acceptance criteria:**
  - **Given** a resolved round, **when** persisted, **then** card, defense, event and resulting integer balance entries are queryable.
  - **Given** a persistence failure, **when** resolution commits, **then** no partial ledger/balance changes remain.
  - **Given** stored inputs/events, **when** replayed, **then** the final state matches the persisted outcome.

#### ROY-STORY-008 — Final rankings and match completion
- **Status:** Todo · **Priority:** P1 · **Depends on:** 003, 007
- **Requirements:** ROYALE-009, 049, 058
- **Implementation:** Complete at round five; rank by portfolio, fewer losses, successful-card count, then shared placement.
- **Acceptance criteria:**
  - **Given** five completed rounds, **when** results are calculated, **then** rankings use tie-breaks in specified order.
  - **Given** an exact tie, **when** all tie-breaks are exhausted, **then** placement is shared.
  - **Given** a finished match, **when** new actions arrive, **then** they are rejected without mutation.

---

## ROY-E02 — Cards, economy, NFTs and abilities

### Feature ROY-F04 — Deck and card effects

#### ROY-STORY-009 — Deal versioned, weighted three-card hands
- **Status:** Todo · **Priority:** P1 · **Depends on:** 002, 006
- **Requirements:** ROYALE-015–020, 062
- **Implementation:** Server-side weighted deck, unique per-match card instance IDs, secure randomness, refill after played cards except round five.
- **Acceptance criteria:**
  - **Given** a new match, **when** dealt, **then** each player receives three private cards.
  - **Given** a played card, **when** round resolves, **then** exactly one card is consumed and the hand refills to three before the next round.
  - **Given** HODL, **when** resolved, **then** no card is consumed; opponents never receive the hand contents.

#### ROY-STORY-010 — Implement Pump, Rug Pull and Diamond Hands
- **Status:** Todo · **Priority:** P1 · **Depends on:** 006, 009
- **Requirements:** ROYALE-010–014, 017, 025, 029
- **Implementation:** Pump +20; Rug steal up to 15; Diamond Hands block first eligible hostile attack and gain 5; clamp all values.
- **Acceptance criteria:**
  - **Given** an unmodified Pump, **when** resolved, **then** player gains 20 MON.
  - **Given** victim has 8 MON, **when** Rug steals, **then** attacker gains at most 8 and victim remains ≥0.
  - **Given** multiple hostile attacks on shielded player, **when** resolved, **then** only first eligible attack is blocked in deterministic order.

#### ROY-STORY-011 — Implement Whale Swap, Airdrop and Chaos
- **Status:** Todo · **Priority:** P1 · **Depends on:** 006, 009–010
- **Requirements:** ROYALE-019–020, 024–025, 030, 062
- **Implementation:** Bounded 20% Whale Swap from snapshots, random Airdrop 5–25, single bounded Chaos modifier per round.
- **Acceptance criteria:**
  - **Given** overlapping swap requests, **when** resolved, **then** each player participates in at most one successful swap.
  - **Given** Airdrop, **when** resolved, **then** awarded amount is 5–25 from server RNG and is logged.
  - **Given** multiple Chaos cards, **when** resolved, **then** no more than one Chaos modifier activates.

### Feature ROY-F05 — Market events and balancing

#### ROY-STORY-012 — Implement five shared market events
- **Status:** Todo · **Priority:** P1 · **Depends on:** 010–011
- **Requirements:** ROYALE-021–025
- **Implementation:** Meme Hype, Market Crash, Whale Incoming, Community Airdrop and Calm Market with configured weights, rounding and caps.
- **Acceptance criteria:**
  - **Given** a round starts, **when** its event is selected, **then** all players see the same event before action submission.
  - **Given** Pump +20 under Meme Hype/Market Crash, **when** resolved, **then** payout is +30/+10 respectively before applicable ability effects.
  - **Given** a modifier, **when** applied, **then** integer rounding, victim limits and global payout caps are respected.

#### ROY-STORY-013 — Cover resolution edge cases with simulations
- **Status:** Todo · **Priority:** P1 · **Depends on:** 008–012
- **Requirements:** ROYALE-025–032, 063, 065
- **Implementation:** Seeded deterministic tests, property tests and simulated full matches with 2 and 6 players; report balance distributions and unresolved rule ambiguities.
- **Acceptance criteria:**
  - **Given** generated legal actions, **when** simulated, **then** no balance becomes negative and no round stalls.
  - **Given** repeated deterministic test fixtures, **when** run, **then** event histories and rankings match.
  - **Given** balance anomalies, **when** found, **then** proposed tuning is documented rather than silently changing game rules.

### Feature ROY-F06 — Character and ability integration

#### ROY-STORY-014 — Reuse NFT selection and guest characters
- **Status:** Todo · **Priority:** P1 · **Depends on:** 001; shared wallet/NFT and guest-character capabilities
- **Requirements:** ROYALE-033–038, 044, 066
- **Implementation:** Use existing character picker and verified NFT snapshot at match start; default Chog for guests/nonholders; store metadata/traits without gameplay bonus.
- **Acceptance criteria:**
  - **Given** a holder with multiple eligible NFTs, **when** entering Royale, **then** exactly one selected NFT is snapshotted after ownership verification.
  - **Given** a guest, **when** entering, **then** they can play with default Chog and identical cards, MON and ability options.
  - **Given** rarity/traits, **when** initializing gameplay, **then** no competitive stat changes occur.

#### ROY-STORY-015 — Implement four Royale abilities
- **Status:** Todo · **Priority:** P1 · **Depends on:** 006, 010–012, 014; shared ability interfaces
- **Requirements:** ROYALE-039–044, 057
- **Implementation:** Implement `diamond_hands_plus`, `alpha_leak`, `double_pump`, `last_second_exit` with once-per-match validation using `SharedAbilityPoolStrategy` and existing execution interface.
- **Acceptance criteria:**
  - **Given** guest or holder, **when** choosing an ability, **then** the same four choices are offered.
  - **Given** an ability already used, **when** invoked again, **then** the server rejects it without effect.
  - **Given** `alpha_leak`, **when** used, **then** next event preview is visible only to its owner until the event becomes public.
  - **Given** `last_second_exit`, **when** the deadline passes, **then** no action revision is accepted; tests confirm its actual additional benefit under the selected lock rules.

---

## ROY-E03 — Client, resilience and release

### Feature ROY-F07 — Angular multiplayer experience

#### ROY-STORY-016 — Build responsive Royale game screen
- **Status:** Todo · **Priority:** P1 · **Depends on:** 001–005, 009, 012
- **Requirements:** ROYALE-045–048, 051, 055
- **Implementation:** Angular route with current event, own hand, opponents, balances, round counter, deadline and submitted indicators; no horizontal scroll on common phone sizes.
- **Acceptance criteria:**
  - **Given** a phone-sized viewport, **when** match is active, **then** all required controls are usable without horizontal scrolling.
  - **Given** a targeted card, **when** selected, **then** only eligible opponents can be targeted.
  - **Given** reduced-motion/keyboard preferences, **when** playing, **then** core controls and status remain accessible.

#### ROY-STORY-017 — Connect action submission and private/public state
- **Status:** Todo · **Priority:** P1 · **Depends on:** 004, 016; shared SignalR infrastructure
- **Requirements:** ROYALE-005–008, 045, 048, 057, 060–061
- **Implementation:** Wire commands, action revision, early lock, deadlines and authoritative state snapshots through shared realtime transport.
- **Acceptance criteria:**
  - **Given** a submitted card, **when** opponents observe the round, **then** they see readiness only, never card or target.
  - **Given** stale or rejected commands, **when** received, **then** UI reflects server error without optimistic balance changes.
  - **Given** deadline expiry, **when** client is idle, **then** server HODL takes effect and UI advances.

#### ROY-STORY-018 — Add reveal, history and results UX
- **Status:** Todo · **Priority:** P1 · **Depends on:** 008, 017
- **Requirements:** ROYALE-049, 051
- **Implementation:** Reveal cards/targets/shields and per-player MON deltas; final ranking and readable round history; cap animation duration.
- **Acceptance criteria:**
  - **Given** a resolved round, **when** reveal displays, **then** all players see consistent card outcomes and MON deltas.
  - **Given** match completion, **when** results display, **then** rankings and tie handling match server results.
  - **Given** reduced motion, **when** reveal occurs, **then** animation is shortened/removed without losing outcome information.

### Feature ROY-F08 — Resilience, QA and deployment

#### ROY-STORY-019 — Reconnect and AFK resilience
- **Status:** Todo · **Priority:** P1 · **Depends on:** 005, 017–018
- **Requirements:** ROYALE-045, 050, 052, 060–061, 064
- **Implementation:** Restore phase, remaining server deadline, own action and public state on reconnect; no disclosure of others' private hands.
- **Acceptance criteria:**
  - **Given** disconnect during decisions, **when** reconnecting, **then** the player sees their latest action and actual remaining deadline.
  - **Given** disconnect beyond deadline, **when** reconnecting, **then** HODL/round result is displayed and game continues.
  - **Given** an unauthorized reconnect attempt, **when** requesting state, **then** hidden information is not returned.

#### ROY-STORY-020 — End-to-end 2–6 player match tests
- **Status:** Todo · **Priority:** P1 · **Depends on:** 013–019
- **Requirements:** ROYALE-001–009, 063–066
- **Implementation:** Automated API/SignalR and browser smoke scenarios for 2 and 6 players, simultaneous commands, late/replayed actions, card conflicts, abilities and reconnect.
- **Acceptance criteria:**
  - **Given** two and six simulated players, **when** matches run, **then** each reaches correct results without deadlock.
  - **Given** malicious/replayed commands, **when** submitted, **then** server state and private data remain protected.
  - **Given** guest and NFT-holder matches, **when** comparing gameplay setup, **then** odds and ability access are equal.

#### ROY-STORY-021 — Verify performance and five-minute target
- **Status:** Todo · **Priority:** P1 · **Depends on:** 020
- **Requirements:** ROYALE-003–004, 065
- **Implementation:** Run repeated full-match timing simulations and instrument phase durations; include server load and six-player network-delay cases.
- **Acceptance criteria:**
  - **Given** standard test conditions, **when** match timer starts, **then** match finishes within five minutes including results.
  - **Given** 6-player latency/AFK cases, **when** exercised, **then** server deadlines continue independently of clients.
  - **Given** any over-budget runs, **when** reviewing metrics, **then** bottlenecks and follow-up fixes are documented before release.

#### ROY-STORY-022 — Release Royale without new infrastructure
- **Status:** Todo · **Priority:** P1 · **Depends on:** 020–021
- **Requirements:** ROYALE-053–059, 063–066
- **Implementation:** Add Royale to existing Nx build/test targets, current Docker images and Compose deployment; document config, smoke checklist and rollback. No extra production service.
- **Acceptance criteria:**
  - **Given** existing deployment stack, **when** deploying Royale, **then** the same Angular/.NET/PostgreSQL/SignalR services host all three games.
  - **Given** a release candidate, **when** CI runs, **then** affected Nx tasks, .NET tests and Life/Race regression checks pass.
  - **Given** a rollback, **when** applied, **then** existing Life/Race gameplay remains available and Royale is safely disabled.

---

## Implementation notes / design questions

1. **Card conflict resolution:** Treat the detailed resolution phases in requirements ROYALE-027–030 as authoritative; document final Whale Swap edge cases with tests before marking ROY-STORY-011 done.
2. **Ability semantics:** `last_second_exit` requires a concrete measurable advantage because normal actions are already revisable until deadline; if it is redundant, propose a requirements change rather than silently replacing it.
3. **Early resolution:** Resolve early only after all players explicitly lock; ordinary submission remains revisable until lock/deadline.
4. **Time target:** 5 × 35-second maximum round budget = 175 seconds, leaving headroom for match start and results. Pre-match lobby waiting is excluded.
5. **Balancing:** Card amounts/weights are initial defaults. Keep them versioned and make tuning a documented decision.
6. **Order:** Start ROY-STORY-001 after necessary shared capabilities exist; work on Royale independently of ongoing Life/Race story numbering.

## AI-agent task template

> Implement **ROY-STORY-XXX** only. Read `AGENTS.md`, `docs/games/chog-royale-requirements.md`, `docs/games/chog-royale-backlog.md` and existing architecture. Inspect shared capabilities and dependencies before editing. Provide a brief plan, implement the smallest safe change, add tests for the story's acceptance criteria, run affected Nx/.NET checks, and report exact outcomes and blockers. Do not change Chog Life or Chog Race behavior or introduce new infrastructure.
