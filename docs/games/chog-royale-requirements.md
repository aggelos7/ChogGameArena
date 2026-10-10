# Chog Royale — Pump or Rug
## Game Requirements v1.0

**Product:** Chog Games Suite  
**Game:** Chog Royale (third game module)  
**Status:** MVP specification; balancing values are initial defaults  
**Language:** English  
**Primary objective:** A complete multiplayer match in 3–5 minutes, including results.

## 1. Product vision and scope

Chog Royale is a mobile-first, simultaneous-action, multiplayer card-battle game for 2–6 players. Each player controls a selected Chog character and tries to finish five short rounds with the highest **fictional** MON portfolio. Cards, market events, bluffing and attacks create rapid reversals. No real tokens are staked, transferred or awarded by gameplay.

### MVP boundaries
- Five rounds per match; all players choose simultaneously, not in sequential turns.
- Three cards visible in each player's hand; one card played per round.
- One shared market event per round; event conditions are visible before choices are locked.
- Private rooms/invite links, guest players and optional wallet connection reuse suite functionality.
- Server-authoritative card draws, RNG, resolution, balances, timers and results.
- No real-money economy, on-chain gameplay, NFT staking or tradable in-game rewards.

## 2. Match structure and timing

| Requirement | Description |
|---|---|
| ROYALE-001 | Support 2–6 players in a room. Host can start when at least two are ready. |
| ROYALE-002 | A match consists of exactly five rounds unless abandoned under suite disconnect rules. |
| ROYALE-003 | Target 3–5 minutes end-to-end, including ready-up, reveal animations and results. |
| ROYALE-004 | Each round has an event announcement (up to 5 s), decision window (15 s), server resolution and reveal (up to 10 s), and transition (up to 5 s). The server is not delayed by client animations. |
| ROYALE-005 | All players submit their actions privately during the same decision window. No action content is disclosed before lock/reveal. |
| ROYALE-006 | Players may change a submitted action until the deadline; only the last valid submission counts. |
| ROYALE-007 | If all players lock early, the server may resolve immediately after a brief confirmation window; do not force waiting for the full timer. |
| ROYALE-008 | A player who times out automatically plays a deterministic safe fallback (`HODL`), with no target or ability. |
| ROYALE-009 | The game ends after round five; highest portfolio wins. Ties are resolved by fewer losses taken, then higher successful-card count, then shared placement if still tied. |

**Timing budget:** Five rounds × 35 seconds maximum = 175 seconds, leaving up to 125 seconds for lobby, network variability, transitions and results within a five-minute match target. The match timer begins when the host starts; prolonged pre-match lobby waiting is outside the game duration target.

## 3. Player economy

| Requirement | Description |
|---|---|
| ROYALE-010 | Every player starts with 100 fictional MON. |
| ROYALE-011 | Balances are integers and cannot fall below zero. |
| ROYALE-012 | No negative balances, borrowing, liquidation debt or real-wallet transactions. |
| ROYALE-013 | Server persists a per-round ledger of card effect, defense, market effect and resulting balance. |
| ROYALE-014 | All balance changes must be bounded by server-configured caps; no client-supplied MON values are trusted. |

## 4. Card system

| Requirement | Description |
|---|---|
| ROYALE-015 | Each player begins with three randomly dealt cards from a versioned, weighted deck. |
| ROYALE-016 | Play exactly one card per round; replenish to three cards after resolution, except after final round. |
| ROYALE-017 | Attack cards require a valid opponent target; no self-targeting. |
| ROYALE-018 | The server validates card ownership, round state, target eligibility and duplicate actions. |
| ROYALE-019 | The same card can appear in multiple hands; card instance IDs are unique per match. |
| ROYALE-020 | The deck and card effects are configured per game, not embedded in shared engine code. |

### Initial card catalog (balancing defaults)

| Card | Initial effect | Notes |
|---|---|---|
| Pump | Gain 20 MON | Positive gain, affected by market event. |
| Rug Pull | Steal up to 15 MON from one opponent | Cannot steal more than victim holds; defense may block. |
| Diamond Hands | Block one incoming targeted attack this round; gain 5 MON | Defensive option; blocks first eligible attack by deterministic order. |
| Whale Swap | Exchange 20% of current balance with one opponent, capped at 20 MON each direction | Simultaneous swap using pre-swap snapshot; can benefit either player. |
| Airdrop | Gain a server-random amount between 5 and 25 MON | RNG is auditable in match event log. |
| Chaos | Trigger one additional random market modifier affecting all players | Drawn from a restricted, bounded chaos pool. |

**HODL fallback:** A timeout or explicit safe action that applies no card effect, retains the player's hand, and grants no automatic reward. `HODL` is not a card and is always available. The ability to choose HODL intentionally is part of the MVP.

## 5. Market events

| Requirement | Description |
|---|---|
| ROYALE-021 | Before each decision window, reveal one shared market event with a short description and explicit effect summary. |
| ROYALE-022 | Initial event pool: Meme Hype (+50% Pump gain), Market Crash (halve Pump gain), Whale Incoming (+5 MON to successful Rug steal, capped by victim balance), Community Airdrop (+5 MON to all players after cards), and Calm Market (no modifier). |
| ROYALE-023 | Event selection uses server RNG and weighted configuration; event is identical for all players. |
| ROYALE-024 | Chaos draws from a separate approved modifier pool; multiple Chaos plays are capped to one modifier per round, selected deterministically by card resolution order. |
| ROYALE-025 | Effects, rounding and maximum payouts must be documented and covered by automated tests. Round monetary calculations down to whole MON. |

## 6. Deterministic simultaneous resolution

| Requirement | Description |
|---|---|
| ROYALE-026 | Server locks actions at deadline and resolves the round as one authoritative operation. |
| ROYALE-027 | Resolution phases: (1) snapshot balances and choices, (2) determine Chaos modifier, (3) activate defenses, (4) apply direct gains and Airdrops, (5) apply targeted Rug attacks, (6) resolve Whale Swaps against a consistent snapshot, (7) apply global post-card market effects, (8) clamp balances and emit ledger/events. |
| ROYALE-028 | Resolve same-phase competing attacks in stable player-seat order, rotated each round to avoid systematic seat advantage. |
| ROYALE-029 | A shield blocks one targeted hostile effect, including Rug Pull or Whale Swap; additional attacks can land. |
| ROYALE-030 | Multiple swaps are applied using a deterministic conflict rule: a player may participate in at most one successful swap per round; earliest eligible swap in rotated order wins. |
| ROYALE-031 | Idempotent action IDs, state versions and round IDs prevent double submissions and duplicate balance changes. |
| ROYALE-032 | Game state transitions and event log allow deterministic replay for test and dispute investigation. |

## 7. Chog NFT and character selection

| Requirement | Description |
|---|---|
| ROYALE-033 | Reuse suite wallet connection and NFT ownership verification for the approved Chog NFT collection on Monad. |
| ROYALE-034 | Show all eligible NFTs owned by the connected wallet; select exactly one character per match. |
| ROYALE-035 | Guests and nonholders can choose a default Chog character with equal gameplay access. |
| ROYALE-036 | Verify ownership at match start and snapshot chosen NFT identity, metadata and traits for that match. |
| ROYALE-037 | NFT ownership, rarity and traits do not increase card odds, starting MON or ability power in MVP. |
| ROYALE-038 | No NFT staking, transfer or wallet signature is required during a round. |

## 8. Ability system

| Requirement | Description |
|---|---|
| ROYALE-039 | Reuse the shared `IAbilityAssignmentStrategy` design and use `SharedAbilityPoolStrategy` for MVP; keep future `TraitBasedAbilityStrategy` extensibility. |
| ROYALE-040 | Every player chooses one Royale-specific ability from the same pool before match start, independently of NFT ownership. |
| ROYALE-041 | Each ability may be activated once per match, with a validated timing window and explicit use feedback. |
| ROYALE-042 | Initial abilities: `diamond_hands_plus` (block one additional eligible attack in a selected round), `alpha_leak` (preview next round's market event after current reveal), `double_pump` (+50% to one Pump gain), and `last_second_exit` (one extra action revision before deadline; no revisions after lock). |
| ROYALE-043 | Abilities are game-specific implementations of the shared ability execution interface and do not require changes to Chog Life or Chog Race. |
| ROYALE-044 | All abilities must be available to NFT holders and guests on equal terms. |

**Balancing note:** `last_second_exit` is primarily a tactical UX advantage; test its value against other choices before public release. An alternative is a once-per-match reroll of one hand card before submitting an action.

## 9. Multiplayer, UX and accessibility

| Requirement | Description |
|---|---|
| ROYALE-045 | Reuse shared rooms, guest identity, lobby readiness, player presence, SignalR connection and reconnect snapshots. |
| ROYALE-046 | Client shows event, 3-card hand, opponents, balances, round counter and countdown on a mobile viewport without horizontal scrolling. |
| ROYALE-047 | A target picker appears only for targeted cards; invalid targets are disabled. |
| ROYALE-048 | Other players' selections remain hidden until server reveal; show only submitted/ready indicators. |
| ROYALE-049 | Reveal screen explains who played what, who was attacked, defense outcomes and net MON changes. |
| ROYALE-050 | Reconnecting players receive current phase, remaining deadline, own submitted action and public state without leaking opponents' hidden actions. |
| ROYALE-051 | UI supports reduced motion, readable contrast, keyboard-accessible actions and text equivalents for effects. |
| ROYALE-052 | Inactive/AFK players follow the timeout HODL rule; a disconnect never freezes the round timer. |

## 10. Architecture and integration

| Requirement | Description |
|---|---|
| ROYALE-053 | Implement Royale as an isolated game rules module using the existing shared multiplayer infrastructure. |
| ROYALE-054 | Keep all game-specific card definitions, market events, economy and abilities inside the Royale module. |
| ROYALE-055 | Integrate into the existing Nx monorepo: Angular game route and UI library where useful; ASP.NET Core game module in the .NET solution. |
| ROYALE-056 | Reuse existing Docker Compose deployment, PostgreSQL persistence and SignalR; do not add new production services for Royale MVP. |
| ROYALE-057 | Match commands use the shared command envelope (`matchId`, `roundId`, `actionId`, `expectedStateVersion`) and expose a Royale `SUBMIT_ACTION` command with card instance ID, optional target and optional ability activation. |
| ROYALE-058 | Emit versioned events such as `RoundStarted`, `ActionSubmitted`, `RoundResolved`, `BalanceChanged`, `AbilityUsed` and `MatchFinished`. |
| ROYALE-059 | Match configuration is versioned and included in persisted match metadata to preserve replay consistency. |

## 11. Security, fairness and testing

| Requirement | Description |
|---|---|
| ROYALE-060 | Do not expose opponents' hands, selected cards, private ability usage or server RNG seeds before reveal. |
| ROYALE-061 | Reject late, invalid, replayed or unauthorized commands without modifying match state. |
| ROYALE-062 | Use secure server-generated randomness for shuffled decks and event selection; no `Math.random()`-style client authority. |
| ROYALE-063 | Unit-test every card, market modifier, ability, conflict rule, clamp and tie-break. |
| ROYALE-064 | Integration-test 2-player and 6-player matches, early locks, deadlines, reconnects, duplicate commands and simultaneous attacks. |
| ROYALE-065 | Run automated full-match simulations to verify no stalled states and that matches meet the five-minute target under expected conditions. |
| ROYALE-066 | Verify NFT holder and guest gameplay parity. |

## 12. MVP acceptance criteria

The Royale MVP is ready when:

1. Two to six players can join a private room and finish a five-round match.
2. All players submit simultaneously; no one waits for another player's individual turn.
3. Every round resolves within its time budget even with AFK or disconnected players.
4. Each card, market event and ability has deterministic, tested behavior.
5. Results show balances, ranking and a readable round history.
6. A wallet holder can choose one eligible Chog NFT; guests play with default Chog characters.
7. NFT rarity and ownership confer no competitive advantage.
8. The game runs through the existing suite infrastructure without a new deployment stack.
9. Automated tests cover core rules, security and reconnection.
10. Real MON tokens never move as a consequence of gameplay.

## 13. Suggested implementation order (after shared suite capabilities exist)

1. Add Royale game identifier, match configuration and route; reuse lobby and player identity.
2. Implement immutable Royale match state, round phases and server timers.
3. Implement deck, hand, card submission and hidden simultaneous choices.
4. Implement deterministic resolution pipeline and balance ledger.
5. Implement six cards and five market events with unit tests.
6. Integrate existing NFT character selection and shared ability assignment.
7. Implement four Royale abilities and validation tests.
8. Build responsive Angular game screen, countdown, targets and reveal animation.
9. Add reconnect handling, end-of-match ranking and match history.
10. Complete multiplayer integration tests, simulations, balancing and release verification.

## 14. Open design decisions before implementation

- Confirm whether `HODL` should remain a zero-reward safe action or receive a small bonus.
- Confirm final card frequencies, damage values, attack caps and event weights after simulations.
- Confirm exact Whale Swap rule after multiplayer balance testing.
- Decide whether early lock can shorten the reveal cadence for very fast matches.
- Decide whether the game will have matchmaking in a later phase; MVP uses private rooms.

**Change control:** Keep this document as `docs/games/chog-royale-requirements.md` in the repository. Extend the existing suite backlog with new Royale stories; do not renumber or rewrite ongoing Chog Life / Chog Race stories.
