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

/** MON staked on the lane taken at a fork, held as an asset until the pawn rejoins the main track (STORY-040, DEC-042). */
export interface LifeStake {
  lane: LifeRoute;
  amount: number;
}

/**
 * A player's pawn, by their seat (the room's `seat`), with their portfolio as the server values it (STORY-040,
 * DEC-042): liquid `mon` (STORY-038), `assets`, `bonuses`, `liabilities` and the total `portfolio`
 * (liquid + assets + bonuses − liabilities, which may be negative). Player ids never reach the client. On a fork,
 * `lane` is the route its player chose to leave by (STORY-039, DEC-041); `stake` is what they staked on it.
 */
export interface LifePawn {
  seat: number;
  tile: number;
  mon: number;
  lane?: LifeRoute;
  stake?: LifeStake;
  assets: number;
  bonuses: number;
  liabilities: number;
  portfolio: number;
  /** The order its player reached the Finish in, from 1 (STORY-041, DEC-043); absent while still on the board. */
  place?: number;
  /** The id of the ability its player armed before a roll, until it acts (STORY-042, DEC-044). */
  armed?: string;
}

/** A stake that paid out as its pawn rejoined the main track. */
export interface LifeMaturity {
  lane: LifeRoute;
  stake: number;
  payout: number;
}

/** One possible return of a lane's stake: its chance and the share of the stake paid back, both in percent. */
export interface LifeStakeOutcome {
  chance: number;
  percent: number;
}

/** What a stake on a lane returns. */
export interface LifeStakeReturns {
  lane: LifeRoute;
  outcomes: LifeStakeOutcome[];
}

/**
 * A seat's latest move (STORY-037, DEC-039): the server's roll, the tiles left and reached, and every tile passed on the
 * way, ending with `to`. A pawn already on the Finish passes without a roll. A move that reached the Finish gives the
 * player's `place` and the finish `bonus` it won, if any (STORY-041, DEC-043). `change` is the MON the tile it stopped on
 * moved (STORY-038, DEC-040), absent for a tile that does nothing. A move that stopped on a fork gets the `lane` chosen
 * there, with `byDefault` when the turn ran out and the rules chose it (STORY-039, DEC-041), and the MON `staked` on it;
 * a move that rejoined the main track with a stake says what it paid out in `matured` (STORY-040, DEC-042).
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
  staked?: number;
  matured?: LifeMaturity;
  place?: number;
  bonus?: number;
  /** MON a Reward Boost added to bonuses on this move's tile (STORY-042, DEC-044). */
  boost?: number;
  /** MON Loss Protection kept from this move's loss. */
  saved?: number;
  /** The signed MON of the worst event outcome a Lucky Reroll discarded for the best one. */
  rerolled?: number;
  /** How many times Lucky Reroll drew the event: 2, or 3 from a rarer Tier (STORY-090, DEC-077). */
  draws?: number;
  /** The route was switched after an Event Preview. */
  switched?: boolean;
}

/** An event outcome as its rule: a fixed `amount` plus `percent` of the player's liquid MON, at least `minimum`. */
export interface LifeEventOutcome {
  chance: number;
  effect: 'Gain' | 'Loss';
  amount: number;
  percent: number;
  minimum: number;
}

/**
 * Your next move on the route you chose after Event Preview (STORY-042, DEC-044): the die, where it lands, the tiles
 * passed and, on an event tile, the outcome drawn. A stake's payout is never shown.
 */
export interface LifePreview {
  lane: LifeRoute;
  roll: number;
  to: number;
  path: number[];
  outcome?: LifeEventOutcome;
}

/** What ended the match: every player still playing reached the Finish, or its time was up (STORY-041, DEC-043). */
export type LifeEnding = 'Finish' | 'TimeUp';

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
  /** The MON staked on the lane taken at a fork, or all the liquid MON held if less; absent when there's no stake. */
  forkStake?: number;
  /** What a stake returns on each lane. */
  stakeReturns?: LifeStakeReturns[];
  /** The bonus only the first player to reach the Finish gets; absent when there's none. */
  finishBonus?: number;
  /** What ended the match, once it's over. */
  ended?: LifeEnding;
  /** The seat that previewed its next move and is deciding whether to keep its route or switch (STORY-042, DEC-044). */
  confirming?: number;
  /** Only for the player who used Event Preview, until they roll: their next move on the route chosen. */
  preview?: LifePreview;
}

/** The command that rolls the die and moves your pawn on your turn. */
export const LIFE_ROLL = 'ROLL';

/** The command that chooses the lane to leave a fork by, with `{ lane }`, after a roll stopped there. */
export const LIFE_CHOOSE_LANE = 'CHOOSE_LANE';

/** The engine's command that uses your locked ability, with `{ abilityId }` (DEC-036). */
export const USE_ABILITY = 'USE_ABILITY';

/** Chog Life's abilities used at a fork before choosing a route; the others are used before a roll (DEC-044). */
export const FORK_ABILITIES: readonly string[] = ['event_preview'];

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
const ENDINGS: readonly unknown[] = ['Finish', 'TimeUp'];

const isRecord = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null;
const isIndex = (value: unknown): value is number => typeof value === 'number' && Number.isInteger(value) && value >= 0;
const isPlace = (value: unknown): boolean => isIndex(value) && value >= 1;
const isStake = (value: unknown): boolean => isRecord(value) && ROUTES.includes(value['lane']) && isIndex(value['amount']);
const isStakeReturns = (value: unknown): boolean =>
  isRecord(value) &&
  ROUTES.includes(value['lane']) &&
  Array.isArray(value['outcomes']) &&
  value['outcomes'].length > 0 &&
  value['outcomes'].every((outcome) => isRecord(outcome) && isIndex(outcome['chance']) && isIndex(outcome['percent']));

const isOutcome = (value: unknown): boolean =>
  isRecord(value) &&
  (value['effect'] === 'Gain' || value['effect'] === 'Loss') &&
  ['chance', 'amount', 'percent', 'minimum'].every((part) => isIndex(value[part]));

const isMove = (move: unknown, tileCount: number): boolean =>
  isRecord(move) &&
  isIndex(move['seat']) &&
  isIndex(move['from']) &&
  isIndex(move['to']) &&
  move['to'] < tileCount &&
  (move['roll'] === undefined || isIndex(move['roll'])) &&
  (move['change'] === undefined || Number.isInteger(move['change'])) &&
  (move['lane'] === undefined || ROUTES.includes(move['lane'])) &&
  (move['byDefault'] === undefined || typeof move['byDefault'] === 'boolean') &&
  (move['staked'] === undefined || isIndex(move['staked'])) &&
  (move['place'] === undefined || isPlace(move['place'])) &&
  (move['bonus'] === undefined || isIndex(move['bonus'])) &&
  (move['boost'] === undefined || isIndex(move['boost'])) &&
  (move['saved'] === undefined || isIndex(move['saved'])) &&
  (move['rerolled'] === undefined || Number.isInteger(move['rerolled'])) &&
  (move['draws'] === undefined || isIndex(move['draws'])) &&
  (move['switched'] === undefined || typeof move['switched'] === 'boolean') &&
  (move['matured'] === undefined ||
    (isRecord(move['matured']) && ROUTES.includes(move['matured']['lane']) && isIndex(move['matured']['stake']) && isIndex(move['matured']['payout']))) &&
  Array.isArray(move['path']) &&
  move['path'].every((tile) => isIndex(tile) && tile < tileCount);

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
      isIndex(pawn['assets']) &&
      isIndex(pawn['bonuses']) &&
      isIndex(pawn['liabilities']) &&
      Number.isInteger(pawn['portfolio']) &&
      (pawn['lane'] === undefined || ROUTES.includes(pawn['lane'])) &&
      (pawn['stake'] === undefined || isStake(pawn['stake'])) &&
      (pawn['place'] === undefined || isPlace(pawn['place'])) &&
      (pawn['armed'] === undefined || typeof pawn['armed'] === 'string'),
  );
  const move = state['lastMove'];
  const validMove = move === undefined || isMove(move, tiles.length);
  const preview = state['preview'];
  const validPreview =
    preview === undefined ||
    (isRecord(preview) &&
      ROUTES.includes(preview['lane']) &&
      isIndex(preview['roll']) &&
      isIndex(preview['to']) &&
      preview['to'] < tiles.length &&
      Array.isArray(preview['path']) &&
      preview['path'].every((tile) => isIndex(tile) && tile < tiles.length) &&
      (preview['outcome'] === undefined || isOutcome(preview['outcome'])));
  const validChoice =
    (state['choosing'] === undefined || isIndex(state['choosing'])) &&
    (state['confirming'] === undefined || isIndex(state['confirming'])) &&
    (state['defaultLane'] === undefined || ROUTES.includes(state['defaultLane']));
  const returns = state['stakeReturns'];
  const validStakes =
    (state['forkStake'] === undefined || isIndex(state['forkStake'])) &&
    (returns === undefined || (Array.isArray(returns) && returns.every(isStakeReturns)));
  const validEnd = (state['finishBonus'] === undefined || isIndex(state['finishBonus'])) && (state['ended'] === undefined || ENDINGS.includes(state['ended']));
  if (tiles.length === 0 || !validTiles || !validPawns || !validMove || !validPreview || !validChoice || !validStakes || !validEnd) {
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

/**
 * What staking `stake` MON on a lane returns, from its outcomes: "Stake 50 MON, get 62 MON back" or, with several
 * outcomes, "Stake 50 MON, get 150 MON back (30%) or 10 MON (70%)". Without MON to stake, it says so.
 */
export function stakeSummary(stake: number, returns: LifeStakeReturns | undefined): string {
  if (!returns || stake <= 0) {
    return 'Nothing to stake';
  }
  const payouts = returns.outcomes.map((outcome) => `${Math.floor((stake * outcome.percent) / 100)} MON`);
  if (payouts.length === 1) {
    return `Stake ${stake} MON, get ${payouts[0]} back`;
  }
  const [first, ...rest] = returns.outcomes.map((outcome, i) => `${payouts[i]}${i === 0 ? ' back' : ''} (${outcome.chance}%)`);
  return `Stake ${stake} MON, get ${first} or ${rest.join(' or ')}`;
}

/** Signed MON: "+11 MON", "−20 MON" or "0 MON". */
export function signedMon(change: number): string {
  return change > 0 ? `+${change} MON` : change < 0 ? `−${-change} MON` : '0 MON';
}

/**
 * An event outcome as its rule: "lose 25% of your liquid MON, at least 20 MON", "gain 25 MON" or "gain 10 MON plus 5%
 * of your liquid MON".
 */
export function outcomeRule(outcome: LifeEventOutcome): string {
  const verb = outcome.effect === 'Gain' ? 'gain' : 'lose';
  const parts = [outcome.amount > 0 ? `${outcome.amount} MON` : '', outcome.percent > 0 ? `${outcome.percent}% of your liquid MON` : '']
    .filter((part) => part)
    .join(' plus ');
  const minimum = outcome.minimum > 0 ? `, at least ${outcome.minimum} MON` : '';
  return `${verb} ${parts || '0 MON'}${minimum}`;
}

/**
 * What a previewed move does (STORY-042, DEC-044): "Roll 6, land on tile 19 (Market): lose 10% of your liquid MON, at
 * least 5 MON", "Roll 1, land on tile 10 (+10 MON)" or "Roll 4, reach the finish".
 */
export function previewSummary(preview: LifePreview, tiles: LifeTile[]): string {
  const tile = tiles[preview.to];
  if (tile?.type === 'Finish') {
    return `Roll ${preview.roll}, reach the finish`;
  }
  const label = tile ? tileLabel(tile) : '';
  const outcome = preview.outcome ? `: ${outcomeRule(preview.outcome)}` : '';
  return `Roll ${preview.roll}, land on tile ${preview.to}${label ? ` (${label})` : ''}${outcome}`;
}

/** A place as shown: 1st, 2nd, 3rd, 4th… (matches hold at most six players). */
export function ordinal(place: number): string {
  return `${place}${({ 1: 'st', 2: 'nd', 3: 'rd' } as Record<number, string>)[place] ?? 'th'}`;
}

/** The time left until `endsAt` from `now` (both in milliseconds) as minutes and seconds, "12:05"; never below "0:00". */
export function timeLeft(endsAt: number, now: number): string {
  const seconds = Math.max(0, Math.ceil((endsAt - now) / 1000));
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
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

/** Pawn colours by seat, shared by every game's board. */
export { SEAT_COLORS, seatColor } from '../seats';

/** A tile placed on the illustrated board (STORY-064, DEC-048): its centre, in board units. */
export interface LifeSpot {
  tile: LifeTile;
  x: number;
  y: number;
}

/** The road from one tile to the next as an SVG path, coloured by the lane it runs on. */
export interface LifeRoad {
  from: number;
  to: number;
  lane: LifeLane;
  path: string;
}

/** The illustrated board: its size in board units, every tile by index, the roads between them and each fork's lane names. */
export interface LifeLayout {
  width: number;
  height: number;
  spots: LifeSpot[];
  roads: LifeRoad[];
  laneNames: { route: LifeRoute; x: number; y: number }[];
}

/** A tile's face as drawn, in its own units, and how much it is scaled up onto the board. */
export const SPOT_FACE_WIDTH = 64;
export const SPOT_FACE_HEIGHT = 54;
export const SPOT_SCALE = 1.3;
/** A tile's size on the illustrated board, in board units. */
export const SPOT_WIDTH = SPOT_FACE_WIDTH * SPOT_SCALE;
export const SPOT_HEIGHT = SPOT_FACE_HEIGHT * SPOT_SCALE;
const COLUMNS = 4;
const COLUMN_STEP = 96;
const columnX = (column: number): number => 58 + column * COLUMN_STEP;
/**
 * How far each lane runs from the middle of the board: 70, or further on a board with more tiles to a row. A folded lane
 * needs at least 106, so its two halves and the lane names between the lanes fit.
 */
const laneSpread = (columns: number, folded: boolean): number => Math.max(folded ? 106 : 70, ((columns - 1) * COLUMN_STEP) / 4);
/** How far each half of a folded lane sits from the lane's middle: a tile's height and a little room apart. */
const FOLD = 46;
/** Room above the first row and below the last for pawns standing on a tile's edge. */
const MARGIN = 52;
// Each step is a tile's size plus a little room for the road between: the horizontal board turns rows and lanes into
// columns, so these also hold a tile's width.
const ROW_GAP = 96;
const LANE_GAP = 92;
const LANE_NAME_GAP = 52;
const U_TURN = 48;

/** Which way the illustrated board runs: down the screen on phones, across it on wide screens (DEC-048). */
export type LifeOrientation = 'vertical' | 'horizontal';

/**
 * The illustrated board laid out from the server's tile graph (STORY-064, DEC-048): the main track in rows of `columns` tiles,
 * left to right and then right to left, and each fork's Safe and Degen lanes as two columns side by side until they join
 * the main track again, so lanes of any length fit. Horizontal, the same board is turned on its side: the track winds
 * through columns from left to right (four by default), and each fork's lanes run as rows with their names between them.
 * There each lane is folded in two, so it takes half the width: its tiles alternate between two rows, top then bottom, a
 * column at a time. Null when a tile can't be placed because no route reaches it.
 */
export function lifeLayout(tiles: LifeTile[], orientation: LifeOrientation = 'vertical', columns = COLUMNS): LifeLayout | null {
  const width = columnX(columns - 1) + 58;
  const folded = orientation === 'horizontal';
  const spread = laneSpread(columns, folded);
  const laneX: Record<LifeRoute, number> = { Safe: width / 2 - spread, Degen: width / 2 + spread };
  const spots = new Map<number, LifeSpot>();
  const laneNames: LifeLayout['laneNames'] = [];
  // Roads into and out of a fork's lanes cross more of a wider board, so they get a little more room to bend.
  const bend = columns > COLUMNS ? 24 : 0;
  let y = MARGIN - ROW_GAP;
  let column = 0;
  let direction = 1;
  let previous: LifeRow['kind'] | null = null;
  // Which of a fork's rows of lane tiles this is: a folded lane takes a new step only every other one.
  let forkRow = 0;
  for (const row of lifeRows(tiles)) {
    if (row.kind === 'main') {
      const next = column + direction;
      if (previous !== 'main' || next < 0 || next >= columns) {
        // A new row: after a full row the track turns back; after a fork it carries on the way it was going.
        if (previous === 'main') {
          direction = -direction;
        }
        column = direction > 0 ? 0 : columns - 1;
        y += ROW_GAP + (previous === 'fork' ? bend : 0);
      } else {
        column = next;
      }
      spots.set(row.tile.index, { tile: row.tile, x: columnX(column), y });
    } else if (row.kind === 'lanes') {
      // Folded, the names go between the lanes, so they need no room of their own before them.
      y += (folded ? 0 : LANE_NAME_GAP) + bend;
      laneNames.push({ route: 'Safe', x: laneX.Safe, y }, { route: 'Degen', x: laneX.Degen, y });
      forkRow = 0;
    } else {
      if (!folded) {
        y += previous === 'lanes' ? LANE_NAME_GAP : LANE_GAP;
      } else if (forkRow % 2 === 0) {
        y += forkRow === 0 ? ROW_GAP : LANE_GAP;
      }
      const half = folded ? (forkRow % 2 === 0 ? -FOLD : FOLD) : 0;
      for (const [route, tile] of [['Safe', row.safe], ['Degen', row.degen]] as const) {
        if (tile) {
          spots.set(tile.index, { tile, x: laneX[route] + half, y });
        }
      }
      forkRow++;
    }
    previous = row.kind;
  }
  if (spots.size !== tiles.length) {
    return null;
  }
  const placed = [...spots.values()].sort((a, b) => a.tile.index - b.tile.index);
  const roads: LifeRoad[] = [];
  for (const from of placed) {
    for (const link of [from.tile.next, from.tile.safeNext, from.tile.degenNext]) {
      const to = link === undefined ? undefined : spots.get(link);
      if (to) {
        roads.push({ from: from.tile.index, to: to.tile.index, lane: to.tile.lane !== 'Main' ? to.tile.lane : from.tile.lane, path: road(from, to) });
      }
    }
  }
  const vertical: LifeLayout = { width, height: y + MARGIN, spots: placed, roads, laneNames };
  return orientation === 'vertical' ? vertical : turned(vertical);
}

/** The smallest scale the horizontal board is drawn at when filling a space, so its tiles stay readable. */
const LEAST_SCALE = 0.9;

/**
 * The horizontal board that fills most of a space of `width` × `height` pixels (DEC-048): from 4 to 9 tiles to a column,
 * the one whose drawing covers the most of it while its tiles are drawn at least at `LEAST_SCALE`, and the fewest tiles
 * to a column when two cover alike. When none is that large, the one drawn largest. Null when the graph can't be laid out.
 */
export function fittedLayout(tiles: LifeTile[], width: number, height: number): LifeLayout | null {
  const options: { layout: LifeLayout; scale: number; area: number }[] = [];
  for (let columns = 4; columns <= 9; columns++) {
    const layout = lifeLayout(tiles, 'horizontal', columns);
    if (!layout) {
      return null;
    }
    const scale = Math.min(width / layout.width, height / layout.height);
    options.push({ layout, scale, area: scale * scale * layout.width * layout.height });
  }
  const readable = options.filter((option) => option.scale >= LEAST_SCALE);
  const candidates = readable.length > 0 ? readable : options;
  const size = (option: (typeof options)[number]): number => (readable.length > 0 ? option.area : option.scale);
  return candidates.reduce((best, option) => (size(option) > size(best) * 1.01 ? option : best)).layout;
}

/** The vertical board turned on its side: every point mirrored across the diagonal, with each fork's lane names moved between its two lanes. */
function turned(board: LifeLayout): LifeLayout {
  return {
    width: board.height,
    height: board.width,
    spots: board.spots.map((spot) => ({ tile: spot.tile, x: spot.y, y: spot.x })),
    roads: board.roads.map((road) => ({ ...road, path: road.path.replace(/(-?\d+(?:\.\d+)?) (-?\d+(?:\.\d+)?)/g, '$2 $1') })),
    laneNames: board.laneNames.map((name) => {
      // A name goes by the first tile of its lane, which sits right under it on the vertical board.
      const first = board.spots.find((spot) => spot.tile.lane === name.route && spot.y > name.y);
      // With the next tile, both rows of a folded lane.
      const second = board.spots.find((spot) => spot.tile.index === first?.tile.next && spot.tile.lane === name.route);
      const rows = [first ?? name, second ?? first ?? name].map((spot) => spot.x);
      return {
        route: name.route,
        x: first?.y ?? name.y,
        // Safe runs above Degen: its name goes under the Safe lane, Degen's over the Degen lane.
        y: name.route === 'Safe' ? Math.max(...rows) + SPOT_HEIGHT / 2 + 14 : Math.min(...rows) - SPOT_HEIGHT / 2 - 7,
      };
    }),
  };
}

/** A straight road along a row or a lane, a U-turn at the end of a row, or an S-bend into and out of a fork. */
function road(a: LifeSpot, b: LifeSpot): string {
  if (a.y === b.y || (a.x === b.x && (a.tile.lane !== 'Main' || b.tile.lane !== 'Main'))) {
    return `M${a.x} ${a.y} L${b.x} ${b.y}`;
  }
  if (a.x === b.x) {
    const out = a.x > columnX(0) ? U_TURN : -U_TURN;
    return `M${a.x} ${a.y} C${a.x + out} ${a.y} ${b.x + out} ${b.y} ${b.x} ${b.y}`;
  }
  const middle = (a.y + b.y) / 2;
  return `M${a.x} ${a.y} C${a.x} ${middle} ${b.x} ${middle} ${b.x} ${b.y}`;
}

/**
 * Where the `i`th of `count` pawns on a tile stands, from the tile's centre: up to three along its top edge, then up to
 * three along its bottom edge, each row centred.
 */
export function pawnOffset(i: number, count: number): { x: number; y: number } {
  const top = Math.min(count, 3);
  const inRow = i < 3 ? top : count - 3;
  const place = i < 3 ? i : i - 3;
  const spacing = inRow === 2 ? 24 : 20;
  return { x: (place - (inRow - 1) / 2) * spacing, y: i < 3 ? -SPOT_HEIGHT / 2 : SPOT_HEIGHT / 2 };
}

/** What a tile says on the illustrated board, short enough to fit: "+20 MON", "Rug pull", "Fork". */
export function spotLabel(tile: LifeTile): string {
  return tile.type === 'Branch' ? 'Fork' : tileLabel(tile);
}
