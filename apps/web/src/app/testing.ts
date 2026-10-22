import { GameSummary } from './games/game-catalog';
import { LifeBoard, LifePawn } from './games/chog-life/life-board-model';
import { LifeResult } from './games/chog-life/life-results';
import { RaceBoard, RaceTile } from './games/chog-race/race-board-model';

/** Catalogue fixture covering every status. */
export const CATALOGUE: GameSummary[] = [
  { id: 'chog-life', name: 'Chog Life', description: 'Life path game.', status: 'Available' },
  { id: 'chog-race', name: 'Chog Race', description: 'Race game.', status: 'ComingSoon' },
  { id: 'chog-fix', name: 'Chog Fix', description: 'Being fixed.', status: 'Maintenance' },
];

/** A small board shaped like the real one: a fork whose lanes differ in length and join at one main tile. */
/**
 * A pawn as the server sends it, with its portfolio (STORY-040, DEC-042): `mon` liquid MON, no assets, bonuses or
 * liabilities unless given, and the total worked out from them.
 */
export function lifePawn(seat: number, tile: number, mon = 100, extra: Partial<LifePawn> = {}): LifePawn {
  const parts = { assets: 0, bonuses: 0, liabilities: 0, ...extra };
  return { seat, tile, mon, ...parts, portfolio: mon + parts.assets + parts.bonuses - parts.liabilities };
}

export const FORK_BOARD: LifeBoard = {
  rulesVersion: 1,
  tiles: [
    { index: 0, type: 'Start', lane: 'Main', next: 1 },
    { index: 1, type: 'Reward', lane: 'Main', next: 2, amount: 20 },
    { index: 2, type: 'Branch', lane: 'Main', safeNext: 3, degenNext: 5 },
    { index: 3, type: 'Reward', lane: 'Safe', next: 4, amount: 10 },
    { index: 4, type: 'Normal', lane: 'Safe', next: 8 },
    { index: 5, type: 'Event', lane: 'Degen', next: 6, event: 'Rug' },
    { index: 6, type: 'Reward', lane: 'Degen', next: 7, amount: 60 },
    { index: 7, type: 'Event', lane: 'Degen', next: 8, event: 'Liquidation' },
    { index: 8, type: 'Loss', lane: 'Main', next: 9, amount: 15 },
    { index: 9, type: 'Finish', lane: 'Main' },
  ],
  pawns: [lifePawn(1, 0), lifePawn(2, 0)],
};

/** A Chog Life result as the server stores it with a rank (STORY-043, DEC-046): 100 MON to start, all statistics zero. */
export function lifeResult(portfolio: number, extra: Partial<LifeResult> = {}): LifeResult {
  return {
    startingPortfolio: 100,
    portfolio,
    liquid: Math.max(portfolio, 0),
    assets: 0,
    bonuses: 0,
    liabilities: Math.max(-portfolio, 0),
    place: null,
    airdrops: 0,
    airdropMon: 0,
    rugsSurvived: 0,
    liquidations: 0,
    biggestWin: 0,
    degenRoutes: 0,
    degenWins: 0,
    degenScore: 0,
    degenScoreVersion: 1,
    ...extra,
  };
}

/** The server's Chog Race version 1 track (STORY-050, DEC-054): 28 tiles with Card, Boost and Setback tiles. */
export const RACE_TILES: RaceTile[] = Array.from({ length: 28 }, (_, index): RaceTile => {
  const jumps: Record<number, RaceTile> = {
    4: { index, type: 'Boost', to: 6 },
    9: { index, type: 'Setback', to: 7 },
    12: { index, type: 'Boost', to: 15 },
    16: { index, type: 'Setback', to: 13 },
    20: { index, type: 'Boost', to: 22 },
    23: { index, type: 'Setback', to: 21 },
  };
  if (index === 0) return { index, type: 'Start' };
  if (index === 27) return { index, type: 'Finish' };
  if ([2, 7, 11, 14, 18, 22, 25].includes(index)) return { index, type: 'Card' };
  return jumps[index] ?? { index, type: 'Normal' };
});

export const RACE_BOARD: RaceBoard = {
  rulesVersion: 1,
  tiles: RACE_TILES,
  pawns: [
    { seat: 1, tile: 0, cards: 0 },
    { seat: 2, tile: 0, cards: 0 },
  ],
  maxHand: 3,
  deck: 24,
  discard: 0,
  hand: [],
};

/**
 * A Chog Royale view as the server sends it (DEC-083, DEC-091): round 2 of 5 under Meme Hype, seat 1 on 115 MON and seat
 * 2 on 85, the viewer's hand of Pump, Rug Pull and Airdrop, and round 1's ledger, in which seat 1's Rug Pull took 15
 * from seat 2, who didn't act and held.
 */
export function royaleState(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    rulesVersion: 2,
    round: 2,
    rounds: 5,
    event: 'MemeHype',
    players: [
      { seat: 1, mon: 115, cards: 3 },
      { seat: 2, mon: 85, cards: 3 },
    ],
    hand: [
      { id: 'c7', card: 'Pump' },
      { id: 'c2', card: 'RugPull' },
      { id: 'c3', card: 'Airdrop' },
    ],
    ledger: [
      {
        round: 1,
        event: 'CalmMarket',
        priority: [1, 2],
        entries: [
          { seat: 1, card: 'RugPull', target: 2, byDefault: false, outcome: 'Applied', amount: 15, lost: 0, market: 0, change: 15, balance: 115 },
          { seat: 2, byDefault: true, amount: 0, lost: 15, market: 0, change: -15, balance: 85 },
        ],
      },
    ],
    ...overrides,
  };
}
