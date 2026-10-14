/** Mirrors the server's tile kinds (DEC-037). */
export type LifeTileType = 'Start' | 'Normal' | 'Reward' | 'Loss' | 'Event' | 'Branch' | 'Finish';
export type LifeLane = 'Main' | 'Safe' | 'Degen';
/** The two lanes a fork offers (STORY-039, DEC-041). */
export type LifeRoute = 'Safe' | 'Degen';
export type LifeEventKind = 'Market' | 'Airdrop' | 'Rug' | 'Liquidation';

/** A tile as the server shows it: links point forward by index; a branch links to its two lanes instead of `next`. */
export interface LifeTile {
  index: number;
  type: LifeTileType;
  lane: LifeLane;
  next?: number;
  safeNext?: number;
  degenNext?: number;
  /** MON won or lost, on Reward and Loss tiles. */
  amount?: number;
  /** The event kind, on Event tiles. */
  event?: LifeEventKind;
}

/**
 * A player's pawn, by their seat (the room's `seat`), with their liquid MON (STORY-038); player ids never reach the
 * client. On a fork, `lane` is the route its player chose to leave by (STORY-039, DEC-041).
 */
export interface LifePawn {
  seat: number;
  tile: number;
  mon: number;
  lane?: LifeRoute;
}

/**
 * A seat's latest move (STORY-037, DEC-039): the server's roll, the tiles left and reached, and every tile passed on the
 * way, ending with `to`. A pawn already on the Finish passes without a roll. `change` is the MON the tile it stopped on
 * moved (STORY-038, DEC-040), absent for a tile that does nothing. A move that stopped on a fork gets the `lane` chosen
 * there, with `byDefault` when the turn ran out and the rules chose it (STORY-039, DEC-041).
 */
export interface LifeMove {
  seat: number;
  roll?: number;
  from: number;
  to: number;
  path: number[];
  change?: number;
  lane?: LifeRoute;
  byDefault?: boolean;
}

/** The Chog Life view in a room snapshot's `state` (STORY-036, DEC-038). */
export interface LifeBoard {
  rulesVersion: number;
  tiles: LifeTile[];
  pawns: LifePawn[];
  /** The latest move, the same for every player; absent before the first roll. */
  lastMove?: LifeMove;
  /** The seat whose roll stopped on a fork and who is choosing a lane (STORY-039, DEC-041). */
  choosing?: number;
  /** The lane taken when the turn runs out before the choice. */
  defaultLane?: LifeRoute;
}

/** The command that rolls the die and moves your pawn on your turn. */
export const LIFE_ROLL = 'ROLL';

/** The command that chooses the lane to leave a fork by, with `{ lane }`, after a roll stopped there. */
export const LIFE_CHOOSE_LANE = 'CHOOSE_LANE';

/**
 * One line of the drawn track: a main tile across the board, the heading of a fork's two lanes, or a fork's safe and
 * degen tiles side by side.
 */
export type LifeRow =
  | { kind: 'main'; tile: LifeTile }
  | { kind: 'lanes' }
  | { kind: 'fork'; safe: LifeTile | null; degen: LifeTile | null };

const TILE_TYPES: readonly string[] = ['Start', 'Normal', 'Reward', 'Loss', 'Event', 'Branch', 'Finish'];
const LANES: readonly string[] = ['Main', 'Safe', 'Degen'];
const ROUTES: readonly unknown[] = ['Safe', 'Degen'];

const isRecord = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null;
const isIndex = (value: unknown): value is number => typeof value === 'number' && Number.isInteger(value) && value >= 0;

/** The board in `state`, or null when the state isn't a Chog Life board this client understands. */
export function toLifeBoard(state: unknown): LifeBoard | null {
  if (!isRecord(state) || !isIndex(state['rulesVersion']) || !Array.isArray(state['tiles']) || !Array.isArray(state['pawns'])) {
    return null;
  }
  const tiles = state['tiles'];
  const validTiles = tiles.every(
    (tile, i) =>
      isRecord(tile) &&
      tile['index'] === i &&
      TILE_TYPES.includes(tile['type'] as string) &&
      LANES.includes(tile['lane'] as string) &&
      ['next', 'safeNext', 'degenNext'].every((link) => tile[link] === undefined || (isIndex(tile[link]) && tile[link] > i && tile[link] < tiles.length)),
  );
  const validPawns = state['pawns'].every(
    (pawn) =>
      isRecord(pawn) &&
      isIndex(pawn['seat']) &&
      isIndex(pawn['tile']) &&
      pawn['tile'] < tiles.length &&
      isIndex(pawn['mon']) &&
      (pawn['lane'] === undefined || ROUTES.includes(pawn['lane'])),
  );
  const move = state['lastMove'];
  const validMove =
    move === undefined ||
    (isRecord(move) &&
      isIndex(move['seat']) &&
      isIndex(move['from']) &&
      isIndex(move['to']) &&
      move['to'] < tiles.length &&
      (move['roll'] === undefined || isIndex(move['roll'])) &&
      (move['change'] === undefined || Number.isInteger(move['change'])) &&
      (move['lane'] === undefined || ROUTES.includes(move['lane'])) &&
      (move['byDefault'] === undefined || typeof move['byDefault'] === 'boolean') &&
      Array.isArray(move['path']) &&
      move['path'].every((tile) => isIndex(tile) && tile < tiles.length));
  const validChoice =
    (state['choosing'] === undefined || isIndex(state['choosing'])) && (state['defaultLane'] === undefined || ROUTES.includes(state['defaultLane']));
  if (tiles.length === 0 || !validTiles || !validPawns || !validMove || !validChoice) {
    return null;
  }
  return state as unknown as LifeBoard;
}

/**
 * The track from start to finish as rows: main tiles one per row, and at each branch its safe and degen lanes side by
 * side until they join the main track again. Tiles no route reaches are left out.
 */
export function lifeRows(tiles: LifeTile[]): LifeRow[] {
  const rows: LifeRow[] = [];
  let index: number | undefined = 0;
  while (index !== undefined && tiles[index]) {
    const tile: LifeTile = tiles[index];
    rows.push({ kind: 'main', tile });
    if (tile.type === 'Branch' && tile.safeNext !== undefined && tile.degenNext !== undefined) {
      const safe = lane(tiles, tile.safeNext);
      const degen = lane(tiles, tile.degenNext);
      rows.push({ kind: 'lanes' });
      for (let i = 0; i < Math.max(safe.tiles.length, degen.tiles.length); i++) {
        rows.push({ kind: 'fork', safe: safe.tiles[i] ?? null, degen: degen.tiles[i] ?? null });
      }
      index = safe.joins;
    } else {
      index = tile.next;
    }
  }
  return rows;
}

/** A lane's tiles from `first` until the main track, and the main tile it joins. */
function lane(tiles: LifeTile[], first: number): { tiles: LifeTile[]; joins: number | undefined } {
  const laneTiles: LifeTile[] = [];
  let index: number | undefined = first;
  while (index !== undefined && tiles[index] && tiles[index].lane !== 'Main') {
    laneTiles.push(tiles[index]);
    index = tiles[index].next;
  }
  return { tiles: laneTiles, joins: index };
}

const EVENT_LABELS: Record<LifeEventKind, string> = {
  Market: 'Market',
  Airdrop: 'Airdrop',
  Rug: 'Rug pull',
  Liquidation: 'Liquidation',
};

/** The tiles of a fork's lane, from the branch at `branch` until it joins the main track; empty if it isn't a branch. */
export function laneTiles(tiles: LifeTile[], branch: number, route: LifeRoute): LifeTile[] {
  const fork = tiles[branch];
  const first = route === 'Safe' ? fork?.safeNext : fork?.degenNext;
  return fork?.type === 'Branch' && first !== undefined ? lane(tiles, first).tiles : [];
}

/**
 * What a lane holds, from its tiles in order: each reward, loss and event, e.g. "+10 MON, Rug pull, +60 MON". A lane of
 * plain tiles says so.
 */
export function laneSummary(tiles: LifeTile[]): string {
  const parts = tiles.filter((tile) => tile.type === 'Reward' || tile.type === 'Loss' || tile.type === 'Event').map(tileLabel);
  return parts.length > 0 ? parts.join(', ') : 'Nothing happens';
}

/** What a tile says on the board. */
export function tileLabel(tile: LifeTile): string {
  switch (tile.type) {
    case 'Start':
      return 'Start';
    case 'Finish':
      return 'Finish';
    case 'Branch':
      return 'Safe or Degen?';
    case 'Reward':
      return `+${tile.amount ?? 0} MON`;
    case 'Loss':
      return `−${tile.amount ?? 0} MON`;
    case 'Event':
      return tile.event ? EVENT_LABELS[tile.event] : 'Event';
    default:
      return '';
  }
}

/**
 * Pawn colours by seat, 1 to 6: distinct and readable with white text. The seat number is printed on every pawn too, so
 * colour is never the only cue.
 */
export const SEAT_COLORS: readonly string[] = ['#5b2bd6', '#0a6c8f', '#b4400b', '#13703a', '#b0175a', '#7a5c00'];

export function seatColor(seat: number): string {
  return SEAT_COLORS[(seat - 1) % SEAT_COLORS.length] ?? SEAT_COLORS[0];
}
