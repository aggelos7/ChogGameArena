# Chog Royale — Balancing

**Story:** STORY-079 · **Decision:** DEC-090 · **Rules:** version 2 (`RoyaleRulesV2`) · **Date:** 2026-10-22

This records how Chog Royale was balanced: how it was simulated, what the simulations found and what changed. Rerun
them after any change to the rules, the abilities or the round engine, and update these tables.

## Method

- **Simulator** (`backend/tests/ChogGames.Games.ChogRoyale.Tests/Simulation/RoyaleSimulator.cs`): plays a match the way
  the API and the round sweep do, with the real `GameRoom`, `ChogRoyaleMatchRules` and `AbilityEngine`, on a virtual
  clock:
  - Every `SUBMIT_ACTION` and `USE_ABILITY` goes through `GameRoom.CheckCommand`, then the rules or the ability engine,
    then `ApplySubmission` or `ApplyAbility`.
  - A round resolves through `ResolveRound` as soon as everyone still playing has locked, or at its deadline.
  - All chance comes from an `IRandomSource` seeded per match, so a match replays exactly from its seed.
  - Simulated players only send what a player could, so a refusal fails the run, and so does a round that doesn't
    resolve exactly once, or a balance below 0, above the cap or unequal to its ledger entry.
- **Players:**
  - Styles: random (any card, one action in ten HODL, a random target), aggressive (Rug Pull or Whale Swap at the
    richest opponent when held) and saver (Pump, Airdrop or Diamond Hands when held).
  - Abilities: one of the four at random. Double Pump is used with the player's first Pump; the others in a planned
    round (Alpha Leak before the last).
  - Pace: each player acts 2–10 s into the decision window (25 s in version 2) and locks four times in five.
  - "Messy" tables: each player lets up to 40% of rounds pass, thinks for up to 30 s (past the window), and one in ten
    leaves before a random round.
- **Report:** `CHOG_BALANCE_REPORT=1 dotnet test tests/ChogGames.Games.ChogRoyale.Tests --filter RoyaleBalanceReportTests
  --logger "console;verbosity=detailed"` (from `backend/`) prints the tables below: 2,000 seeded matches per row, under a
  minute in total. CI runs the lighter guards in `RoyaleSimulationTests`. A win is a first place, shared or not.

## Findings

### No stalls

No simulated match got stuck or had a legal command refused, and no balance went below zero. Every completed match
lasted at most 3 min 30 s, within the five-minute target (ROYALE-003, ROYALE-065): a table where nobody acts takes exactly
that (5 s, then five 25 s windows with 20 s between them), and every round that everyone locks resolves at once. With
version 1's timing (15 s windows, 20 s between rounds) the longest match was 2 min 40 s; version 2 gives players more time
to decide, at the product owner's request.
Two-player messy tables end early when one of the two leaves (abandoned).

### Version 1 against version 2

In version 1, Whale Swap and Chaos moved no MON for their player on average (+0.1 and 0 MON), and the players who played
them won 15% and 14% of the time against a fair 25%, where Pump, Rug Pull and Airdrop players won 27–28%. Version 2 pays
both: Chaos +10 MON to its player and a successful Whale Swap +10 on top of the exchange. Their players now win about 20%,
beside Diamond Hands (22%), whose value is defensive. Seats, styles and abilities stay within about 3 points of a fair
share, so nothing else changed. (A 5 MON swap bonus was tried too: Whale Swap players won 17%.)

## Tables (version 2)

#### Duration and endings

| Table | p10 | Median | p90 | Max | Rounds resolved early | Completed | Abandoned |
|---|---|---|---|---|---|---|---|
| 2 players, engaged (2–10 s) | 2:09 | 2:35 | 2:57 | 3:30 | 63.2% | 100.0% | 0.0% |
| 2 players, messy | 1:35 | 3:17 | 3:30 | 3:30 | 27.6% | 81.1% | 18.9% |
| 4 players, engaged (2–10 s) | 2:34 | 2:57 | 3:14 | 3:30 | 40.5% | 100.0% | 0.0% |
| 4 players, messy | 3:20 | 3:30 | 3:30 | 3:30 | 9.4% | 99.8% | 0.3% |
| 6 players, engaged (2–10 s) | 2:43 | 3:13 | 3:30 | 3:30 | 26.0% | 100.0% | 0.0% |
| 6 players, messy | 3:28 | 3:30 | 3:30 | 3:30 | 3.1% | 100.0% | 0.0% |

#### Final MON (engaged)

| Table | Min | p10 | Median | p90 | Max | Winner's median | Winner's lead over second (median) | Shared first |
|---|---|---|---|---|---|---|---|---|
| 2 players | 70 | 126 | 155 | 185 | 250 | 167 | 20 | 0.5% |
| 4 players | 75 | 130 | 155 | 185 | 250 | 176 | 10 | 0.8% |
| 6 players | 80 | 130 | 157 | 185 | 243 | 180 | 9 | 0.8% |

#### Cards (engaged, all table sizes)

| Card | Share of plays | Successful | Mean MON when successful | Win rate of players who played it | Fair share |
|---|---|---|---|---|---|
| Pump | 31.7% | 100.0% | 21.2 | 28.4% | 25.0% |
| RugPull | 19.6% | 82.3% | 15.5 | 27.4% | 25.1% |
| DiamondHands | 15.6% | 100.0% | 5.0 | 21.5% | 25.0% |
| WhaleSwap | 9.6% | 76.7% | 10.0 | 19.9% | 25.1% |
| Airdrop | 16.1% | 100.0% | 15.0 | 26.1% | 25.0% |
| Chaos | 7.4% | 88.5% | 10.0 | 20.2% | 24.9% |

#### Cards: version 1 against version 2 (engaged, all table sizes)

| Card | Mean MON when successful, v1 | v2 | Win rate of players who played it, v1 | v2 |
|---|---|---|---|---|
| Pump | 21.2 | 21.2 | 28.5% | 28.4% |
| RugPull | 15.5 | 15.5 | 26.9% | 27.4% |
| DiamondHands | 5.0 | 5.0 | 22.9% | 21.5% |
| WhaleSwap | 0.1 | 10.0 | 14.8% | 19.9% |
| Airdrop | 15.0 | 15.0 | 27.4% | 26.1% |
| Chaos | 0.0 | 10.0 | 13.6% | 20.2% |

#### Abilities, styles and seats (engaged, all table sizes)

| Group | Win rate | Fair share |
|---|---|---|
| Ability diamond_hands_plus | 26.7% | 25.0% |
| Ability alpha_leak | 23.0% | 25.0% |
| Ability double_pump | 27.9% | 25.0% |
| Ability stop_loss | 23.1% | 25.0% |
| Style Random | 24.2% | 25.0% |
| Style Aggressive | 24.6% | 25.0% |
| Style Saver | 26.8% | 25.0% |
| Seat 1 of 2 | 50.6% | 50.0% |
| Seat 2 of 2 | 50.0% | 50.0% |
| Seat 1 of 4 | 26.3% | 25.0% |
| Seat 4 of 4 | 23.4% | 25.0% |
| Seat 1 of 6 | 17.4% | 16.7% |
| Seat 6 of 6 | 16.0% | 16.7% |

#### Without abilities (engaged)

| Table | Median final MON | Winner's median | Shared first |
|---|---|---|---|
| 2 players | 155 | 165 | 0.5% |
| 4 players | 155 | 174 | 1.1% |
| 6 players | 155 | 177 | 1.7% |
