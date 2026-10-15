/** Mirrors the server's tile kinds (DEC-054). */
export type RaceTileType = 'Start' | 'Normal' | 'Card' | 'Boost' | 'Setback' | 'Finish';

/** A tile as the server shows it: the track runs by index; a Boost or Setback sends a pawn that lands on it `to` a tile. */
export interface RaceTile {
  index: number;
  type: RaceTileType;
  to?: number;
}

/** A player's pawn, by their seat (the room's `seat`); player ids never reach the client. */
export interface RacePawn {
  seat: number;
  tile: number;
}

/** The Chog Race view in a room snapshot's `state` (STORY-050, DEC-054). */
export interface RaceBoard {
  rulesVersion: number;
  tiles: RaceTile[];
  pawns: RacePawn[];
}

/** Where a tile sits on the drawn track: its grid row and column, from 1. */
export interface RaceSpot {
  tile: RaceTile;
  row: number;
  column: number;
}

/** The fewest and most tiles a row of the drawn track holds; 4 fits a 375px phone (DEC-054). */
export const MIN_COLUMNS = 4;
export const MAX_COLUMNS = 10;

/** The least width a tile is drawn at, in pixels, so pawns and labels stay legible and tappable. */
export const TILE_WIDTH = 84;

const TILE_TYPES: readonly string[] = ['Start', 'Normal', 'Card', 'Boost', 'Setback', 'Finish'];

const isRecord = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null;
const isIndex = (value: unknown): value is number => typeof value === 'number' && Number.isInteger(value) && value >= 0;

/** The board in `state`, or null when the state isn't a Chog Race board this client understands. */
export function toRaceBoard(state: unknown): RaceBoard | null {
  if (!isRecord(state) || !isIndex(state['rulesVersion']) || !Array.isArray(state['tiles']) || !Array.isArray(state['pawns'])) {
    return null;
  }
  const tiles = state['tiles'];
  const jumps = (type: unknown) => type === 'Boost' || type === 'Setback';
  const validTiles = tiles.every(
    (tile, i) =>
      isRecord(tile) &&
      tile['index'] === i &&
      TILE_TYPES.includes(tile['type'] as string) &&
      (jumps(tile['type']) ? isIndex(tile['to']) && tile['to'] < tiles.length : tile['to'] === undefined),
  );
  const validPawns = state['pawns'].every((pawn) => isRecord(pawn) && isIndex(pawn['seat']) && isIndex(pawn['tile']) && pawn['tile'] < tiles.length);
  if (tiles.length === 0 || !validTiles || !validPawns) {
    return null;
  }
  return state as unknown as RaceBoard;
}

/** How many tiles a row holds on a board `width` pixels wide. */
export function raceColumns(width: number): number {
  return Math.min(MAX_COLUMNS, Math.max(MIN_COLUMNS, Math.floor(width / TILE_WIDTH)));
}

/**
 * The track laid out as a serpentine in rows of `columns` tiles: the first row left to right, the next right to left, and
 * so on, so consecutive tiles always touch and the whole race fits on a phone screen.
 */
export function raceLayout(tiles: RaceTile[], columns: number): RaceSpot[] {
  return tiles.map((tile, i) => {
    const row = Math.floor(i / columns);
    const step = i % columns;
    return { tile, row: row + 1, column: (row % 2 === 0 ? step : columns - 1 - step) + 1 };
  });
}

/** What a tile says on the board. */
export function raceTileLabel(tile: RaceTile): string {
  switch (tile.type) {
    case 'Start':
      return 'Start';
    case 'Finish':
      return 'Finish';
    case 'Card':
      return 'Card';
    case 'Boost':
      return `Boost to ${tile.to}`;
    case 'Setback':
      return `Back to ${tile.to}`;
    default:
      return '';
  }
}
