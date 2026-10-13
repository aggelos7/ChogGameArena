import { GameSummary } from './games/game-catalog';
import { LifeBoard } from './games/chog-life/life-board-model';

/** Catalogue fixture covering every status. */
export const CATALOGUE: GameSummary[] = [
  { id: 'chog-life', name: 'Chog Life', description: 'Life path game.', status: 'Available' },
  { id: 'chog-race', name: 'Chog Race', description: 'Race game.', status: 'ComingSoon' },
  { id: 'chog-fix', name: 'Chog Fix', description: 'Being fixed.', status: 'Maintenance' },
];

/** A small board shaped like the real one: a fork whose lanes differ in length and join at one main tile. */
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
  pawns: [
    { seat: 1, tile: 0 },
    { seat: 2, tile: 0 },
  ],
};
