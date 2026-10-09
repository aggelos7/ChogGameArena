import { GameSummary } from './games/game-catalog';

/** Catalogue fixture covering every status. */
export const CATALOGUE: GameSummary[] = [
  { id: 'chog-life', name: 'Chog Life', description: 'Life path game.', status: 'Available' },
  { id: 'chog-race', name: 'Chog Race', description: 'Race game.', status: 'ComingSoon' },
  { id: 'chog-fix', name: 'Chog Fix', description: 'Being fixed.', status: 'Maintenance' },
];
