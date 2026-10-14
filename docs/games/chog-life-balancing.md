# Chog Life — Balancing and Multiplayer QA

**Story:** STORY-044 · **Decision:** DEC-047 · **Rules:** version 2 (`LifeRulesV2`) · **Date:** 2026-10-15

This records how Chog Life was balanced: how it was tested, what the simulations found and what changed. The
simulations play whole matches through the real engine, rules and abilities. Rerun them after any change to the rules,
the abilities or the turn engine, and update these tables.

## Method

- **Simulator** (`backend/tests/ChogGames.Games.ChogLife.Tests/Simulation/LifeSimulator.cs`): plays a match the way the
  API and the turn timer do, using the real `GameRoom`, `ChogLifeMatchRules` and `AbilityEngine`, on a virtual clock:
  - Every command goes through `GameRoom.CheckCommand`, then the rules (or `AbilityEngine.Use`), then
    `ApplyCommand`/`ApplyAbility`.
  - A turn nobody plays runs out through `IMatchRules.TimeOutTurn` and `GameRoom.TimeOutTurn` (3 misses in a row forfeit).
  - The match ends through `IMatchRules.EndMatch` and `GameRoom.End` when everyone has finished or the 20-minute cap is up.
  - All chance comes from an `IRandomSource` seeded per match, so any match replays exactly from its seed.
  - Simulated players only send commands that are legal at that moment, so a refusal fails the run. Every step must
    change the room, and a step that doesn't is reported as a stuck turn. Invariants are checked on every step: the
    turn belongs to a player still playing who hasn't finished, and the clock never passes the cap.
- **Players:**
  - Lane strategies: always Safe, always Degen, or a coin flip at each fork.
  - Abilities: one of the four, or none. Boost, Protection and Reroll are armed before the first roll. Event Preview is
    used at the first fork, keeping the lane unless the preview lands on a loss.
  - Pace: each action takes a think time. Engaged players take 2–8 s and slow players 8–20 s; the turn lasts 25 s
    (DEC-045).
  - Absence: AFK players let a share of their turns run out. Disconnected players act on nothing for a while, then come
    back. Leavers forfeit at a set time.
  - "Messy" tables mix all of these. Each player lets up to 40% of turns run out, a third drop out for up to 3 minutes,
    and one in ten leaves.
- **Real multiplayer QA** (`backend/tests/ChogGames.Api.Tests/ChogLife/ChogLifeMultiplayerTests.cs`): whole matches of
  2, 4 and 6 guests through the REST API and the SignalR hub, on PostgreSQL, with seeded server chance:
  - Each player has a live connection.
  - One player lets two turns run out and comes back; the turn timer is swept as in production.
  - One player reloads mid-match: a new browser with only the cookie, `GET /api/rooms/current`, then a new hub
    subscription that returns the same snapshot.
  - With 4 or more players, one leaves.
  - Before every step, a player who isn't on turn and an outsider each try to roll (`409 not_your_turn` and
    `404 room_not_found`), and the version must not change.
  - Every match reaches the Finish. Every player sees the same final room over REST and live, ranks and results are
    stored, and stats are recorded once.
- **Report:** `CHOG_BALANCE_REPORT=1 dotnet test tests/ChogGames.Games.ChogLife.Tests --filter LifeBalanceReportTests
  --logger "console;verbosity=detailed"` (from `backend/`) prints the tables below: 2,000 seeded matches per row, about
  a minute in total. CI runs the lighter guards in `LifeSimulationTests` instead.

## Findings

### No stuck turns

Not one simulated match got stuck, and no legal move was refused. That covers over 40,000 report matches plus the
suite's own runs, at 2, 4 and 6 players, with every mix of lanes, abilities, AFK, disconnects and leavers. Every match
ended within its cap: everyone finished, the cap was reached, or forfeits left fewer than two players. A table where
nobody plays forfeits seat by seat, in exactly `(3n − 1) × 25 s` (2 min 5 s for two players, 7 min 5 s for six). The
three real API matches finished the same way.

### Duration and endings (version 2)

| Table | p10 min | Median min | p90 min | Max min | Avg turns | Finish | Time up | Abandoned |
|---|---|---|---|---|---|---|---|---|
| 2 players, engaged (2–8 s an action) | 1.8 | 2.1 | 2.4 | 3.0 | 19 | 100% | 0% | 0% |
| 2 players, slow (8–20 s an action) | 4.7 | 5.5 | 6.3 | 7.8 | 20 | 100% | 0% | 0% |
| 2 players, 30% of turns AFK | 2.4 | 4.4 | 6.2 | 9.6 | 21 | 58% | 0% | 42% |
| 2 players, messy | 1.9 | 3.2 | 5.0 | 8.9 | 18 | 51% | 0% | 49% |
| 4 players, engaged | 3.7 | 4.2 | 4.6 | 5.3 | 38 | 100% | 0% | 0% |
| 4 players, slow | 9.9 | 11.0 | 12.2 | 14.1 | 40 | 100% | 0% | 0% |
| 4 players, 30% of turns AFK | 7.4 | 9.3 | 11.9 | 19.1 | 47 | 96% | 0% | 4% |
| 4 players, messy | 5.3 | 7.3 | 9.6 | 13.5 | 40 | 91% | 0% | 9% |
| 6 players, engaged | 5.7 | 6.2 | 6.8 | 7.7 | 57 | 100% | 0% | 0% |
| 6 players, slow | 15.2 | 16.5 | 17.9 | 20.0 | 59 | 100% | 0% | 0% |
| 6 players, 30% of turns AFK | 11.6 | 14.2 | 17.0 | 20.0 | 70 | 99% | 1% | 0% |
| 6 players, messy | 8.6 | 11.0 | 13.9 | 20.0 | 61 | 100% | 0% | 0% |

- **Length:** a pawn needs about 9–10 rolls to reach the Finish, plus a lane choice at each of the two forks. Engaged
  matches therefore last about 2, 4 and 6 minutes, well under the 15–20 minutes in the requirements. A board of 30–40
  tiles with a die can't get near that: a four-sided die only adds about 25%. The product owner revised the target
  (DEC-047): Chog Life plays in 2–8 minutes when engaged and up to about 15 with slow or absent players, under the
  20-minute cap, which is unchanged.
- **Two-player AFK:** three missed turns in a row forfeit (DEC-027), and with two players a forfeit abandons the match.
  So 42% of two-player matches where each player skips 30% of turns end abandoned. That's the engine working as agreed.
- **Fork turns can run out:** a fork turn can be four actions in one 25-second turn (roll, Event Preview, choose, keep).
  An engaged player at the slow end of 8 s per action then runs out of time, which happened in 0.2% of engaged turns.
  The rules then take the default lane, or keep the previewed one, and the match goes on.

### Finish bonus: version 1 (100 MON) against version 2 (50 MON)

The first player to finish won 81% of two-player matches with a 100 MON bonus, against LIFE-007 ("reaching the finish
first does not guarantee victory"). The bonus also gave turn order a visible edge, because seat 1 finishes first most
often. With 50 MON, racing still pays without deciding the match:

| Table | First finisher wins, v1 | v2 | Seat 1 wins, v1 | v2 | Fair share |
|---|---|---|---|---|---|
| 2 players | 81% | 67% | 54% | 51% | 50% |
| 4 players | 61% | 41% | 30% | 28% | 25% |
| 6 players | 50% | 31% | 22% | 20% | 17% |

Other bonuses tried (1,000 matches each): with 25 MON the first finisher wins 57/31/21%, and with none 48/23/14% (the
same as anyone else), so racing would stop mattering.

### Lanes (version 2, engaged, abilities mixed)

"Wins" counts shared first places. "Below start" means a final portfolio under the 100 MON start.

| Table | Lanes | Wins | Avg portfolio | Median | Debt at end | Below start |
|---|---|---|---|---|---|---|
| 2 players (fair 50%) | Safe | 51% | 166 | 168 | 0% | 11% |
| | Degen | 46% | 155 | 142 | 6% | 34% |
| | Coin flip | 53% | 163 | 157 | 2% | 25% |
| 4 players (fair 25%) | Safe | 23% | 159 | 159 | 0% | 12% |
| | Degen | 25% | 140 | 125 | 6% | 41% |
| | Coin flip | 27% | 152 | 147 | 2% | 28% |
| 6 players (fair 17%) | Safe | 14% | 153 | 153 | 0% | 14% |
| | Degen | 19% | 134 | 119 | 6% | 44% |
| | Coin flip | 17% | 145 | 136 | 2% | 31% |

- **Safe is the lower-variance route (LIFE-005):** the best average and almost never in debt.
- **Degen is the high-risk route:** a lower average and the most debt, but it wins more as tables grow, because one big
  swing beats five careful players. Both lanes stay within a few points of a fair share, so neither is the obvious
  pick. No change.

### Abilities (version 2, engaged)

| Ability | Wins, 2 players (fair 50%) | 4 players (fair 25%) | 6 players (fair 17%) | Avg portfolio, 4 players |
|---|---|---|---|---|
| Reward Boost | 52% | 25% | 17% | 155 |
| Loss Protection | 52% | 26% | 19% | 154 |
| Event Preview | 51% | 25% | 15% | 152 |
| Lucky Reroll | 46% | 25% | 18% | 146 |
| None | 49% | 24% | 14% | 144 |

Every ability beats having none, and none is worth more than about 3 points of win share. Loss Protection is slightly
ahead. Lucky Reroll is slightly behind in two-player matches, where it often stays armed waiting for an event with
several outcomes. That's within noise for a choice every player gets, so the catalog stays at version 1 (DEC-044).

### Degen Score

The average is 39 and 2% of players reach 100 (formula version 1, DEC-046). It separates risk-takers from careful
players without saturating, so the weights stay.

## Guards in CI

`LifeSimulationTests` (300–600 seeded matches per case, about 10 s) fails if a later change:

- leaves a match stuck, or refuses a legal move;
- stops engaged matches from reaching the finish;
- changes how AFK players forfeit, how reconnects and leavers are handled, or how the time cap ranks players;
- moves the engaged median out of 1–3, 3–5 or 5–7.5 minutes (2, 4 or 6 players);
- pushes the first finisher's win share out of 55–75%, 30–50% or 20–40%;
- moves seat 1 more than 6 points from a fair share, or any lane strategy or ability more than 8 points.

Changing any of these values means a new rules version (or catalog or Degen Score version) and new tables here.
