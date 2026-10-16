/** Mirrors the server's tile kinds (DEC-054). */
export type RaceTileType = 'Start' | 'Normal' | 'Card' | 'Boost' | 'Setback' | 'Finish';

/** A tile as the server shows it: the track runs by index; a Boost or Setback sends a pawn that lands on it `to` a tile. */
export interface RaceTile {
  index: number;
  type: RaceTileType;
  to?: number;
}

/**
 * Mirrors the server's cards (RACE-005, DEC-056). Pump, Whale, Airdrop, FOMO and Reverse are played on your turn
 * (STORY-053, DEC-057); Rug, HODL and Liquidated come with STORY-054.
 */
export type RaceCard = 'Pump' | 'Rug' | 'Hodl' | 'Whale' | 'Airdrop' | 'Liquidated' | 'Fomo' | 'Reverse';

/** What landing on a Card tile drew (DEC-056): a card, or nothing because the hand was full or no card was left. */
export type RaceDraw = 'Drawn' | 'HandFull' | 'NoCards';

/** A player's pawn, by their seat (the room's `seat`); player ids never reach the client. */
export interface RacePawn {
  seat: number;
  tile: number;
  /** How many cards the player holds (STORY-052); which ones only they see. */
  cards?: number;
}

/**
 * A seat's latest move (STORY-051, DEC-055): the server's roll, the tile it left, the tile it `landed` on and the tile it
 * ended `to` after a Boost or Setback, and what the tile landed on does (absent for a Normal tile). On a Card tile, what
 * was drawn and whether the discard pile was reshuffled into the deck first (STORY-052, DEC-056); the `card` is sent only
 * to the player who drew it.
 */
export interface RaceMove {
  seat: number;
  roll: number;
  from: number;
  landed: number;
  to: number;
  effect?: Exclude<RaceTileType, 'Start' | 'Normal'>;
  draw?: RaceDraw;
  card?: RaceCard;
  reshuffled?: boolean;
  /** The card the player then played, if any (STORY-053, DEC-057). */
  play?: RacePlay;
}

/** An opponent Reverse moved back: the tile it `landed` on, and `to` after a Boost or Setback there (the `effect`) sent it on. */
export interface RacePush {
  seat: number;
  from: number;
  landed: number;
  to: number;
  effect?: 'Boost' | 'Setback';
}

/**
 * A card played (STORY-053, DEC-057). Pump and FOMO give the signed `steps` moved, Airdrop the `roll`; all three where
 * the player went as a roll does, with the card `drawn` sent only to them. Whale gives the tiles the player went `from`
 * and `to` and the `target` seat, which went the other way; Reverse every opponent it `pushed` back.
 */
export interface RacePlay {
  card: RaceCard;
  steps?: number;
  roll?: number;
  from?: number;
  landed?: number;
  to?: number;
  effect?: RaceMove['effect'];
  draw?: RaceDraw;
  drawn?: RaceCard;
  reshuffled?: boolean;
  target?: number;
  pushed?: RacePush[];
}

/** How far the cards move pawns, from the match's rules version (DEC-057). */
export interface RaceCardValues {
  pump: number;
  fomoForward: number;
  fomoBack: number;
  fomoForwardChance: number;
  reverse: number;
}

/** The current player's phase (DEC-055): rolling, or playing an optional card before ending the turn. */
export type RacePhase = 'Move' | 'Play';

/** The Chog Race view in a room snapshot's `state` (STORY-050, DEC-054; STORY-051, DEC-055). */
export interface RaceBoard {
  rulesVersion: number;
  tiles: RaceTile[];
  pawns: RacePawn[];
  lastMove?: RaceMove;
  /** Absent once the match is over. */
  phase?: RacePhase;
  /** The winner's seat, once someone reached the Finish. */
  winner?: number;
  /** The most cards a hand holds (STORY-052, DEC-056). */
  maxHand?: number;
  /** How many cards are left in the deck and on the discard pile; the deck's order is never sent. */
  deck?: number;
  discard?: number;
  /** Your own cards, while you're playing; nobody else's are ever sent. */
  hand?: RaceCard[];
  /** Which of your cards you can play on your turn, each kind once (STORY-053, DEC-057). */
  playable?: RaceCard[];
  cardValues?: RaceCardValues;
}

/** Rolls, moves and resolves the tile landed on, in the Move phase (DEC-055). */
export const RACE_ROLL = 'ROLL';

/** Ends the turn in the Play phase, without playing a card (DEC-055). */
export const RACE_END_TURN = 'END_TURN';

/** Plays a card in the Play phase, which ends the turn (DEC-057). */
export const RACE_PLAY_CARD = 'PLAY_CARD';

/** What a card does, in a short sentence, with the match's values when the server sent them (DEC-057). */
export function raceCardDescription(card: RaceCard, values?: RaceCardValues): string {
  switch (card) {
    case 'Pump':
      return values ? `Move ${values.pump} forward.` : 'Move forward.';
    case 'Fomo':
      return values
        ? `${values.fomoForwardChance}% chance to move ${values.fomoForward} forward, otherwise ${values.fomoBack} back.`
        : 'Move forward or back, at random.';
    case 'Airdrop':
      return 'Roll again.';
    case 'Whale':
      return 'Swap places with an opponent you pick.';
    case 'Reverse':
      return values ? `Every opponent moves ${values.reverse} back.` : 'Every opponent moves back.';
    case 'Rug':
      return 'Send an opponent back. Can’t be played yet.';
    case 'Hodl':
      return 'Block an attack on you. Can’t be played yet.';
    case 'Liquidated':
      return 'An opponent skips their next turn. Can’t be played yet.';
  }
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

/** Each card's name as players know it. */
export const RACE_CARD_NAMES: Readonly<Record<RaceCard, string>> = {
  Pump: 'Pump',
  Rug: 'Rug',
  Hodl: 'HODL',
  Whale: 'Whale',
  Airdrop: 'Airdrop',
  Liquidated: 'Liquidated',
  Fomo: 'FOMO',
  Reverse: 'Reverse',
};

const CARDS: readonly string[] = Object.keys(RACE_CARD_NAMES);
const isCard = (value: unknown): value is RaceCard => typeof value === 'string' && CARDS.includes(value);

const isRecord = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null;
const isIndex = (value: unknown): value is number => typeof value === 'number' && Number.isInteger(value) && value >= 0;
const isOptional = (value: unknown, check: (value: unknown) => boolean) => value === undefined || check(value);
const EFFECTS: readonly string[] = ['Card', 'Boost', 'Setback', 'Finish'];
const DRAWS: readonly string[] = ['Drawn', 'HandFull', 'NoCards'];

/** A card play as the server sends it, its tiles on a track of `tiles` tiles. */
function isPlay(play: unknown, tiles: number): boolean {
  const isTile = (value: unknown) => isIndex(value) && value < tiles;
  return (
    isRecord(play) &&
    isCard(play['card']) &&
    isOptional(play['steps'], (steps) => typeof steps === 'number' && Number.isInteger(steps)) &&
    ['roll', 'target'].every((key) => isOptional(play[key], isIndex)) &&
    ['from', 'landed', 'to'].every((key) => isOptional(play[key], isTile)) &&
    isOptional(play['effect'], (effect) => EFFECTS.includes(effect as string)) &&
    isOptional(play['draw'], (draw) => DRAWS.includes(draw as string)) &&
    isOptional(play['drawn'], isCard) &&
    isOptional(play['reshuffled'], (reshuffled) => typeof reshuffled === 'boolean') &&
    isOptional(
      play['pushed'],
      (pushed) =>
        Array.isArray(pushed) &&
        pushed.every(
          (push) =>
            isRecord(push) &&
            isIndex(push['seat']) &&
            ['from', 'landed', 'to'].every((key) => isTile(push[key])) &&
            isOptional(push['effect'], (effect) => effect === 'Boost' || effect === 'Setback'),
        ),
    )
  );
}

const isCardValues = (values: unknown): boolean =>
  isRecord(values) && ['pump', 'fomoForward', 'fomoBack', 'fomoForwardChance', 'reverse'].every((key) => isIndex(values[key]));

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
  const validPawns = state['pawns'].every(
    (pawn) =>
      isRecord(pawn) &&
      isIndex(pawn['seat']) &&
      isIndex(pawn['tile']) &&
      pawn['tile'] < tiles.length &&
      (pawn['cards'] === undefined || isIndex(pawn['cards'])),
  );
  const move = state['lastMove'];
  const validMove =
    move === undefined ||
    (isRecord(move) &&
      isIndex(move['seat']) &&
      isIndex(move['roll']) &&
      [move['from'], move['landed'], move['to']].every((tile) => isIndex(tile) && tile < tiles.length) &&
      (move['effect'] === undefined || EFFECTS.includes(move['effect'] as string)) &&
      (move['draw'] === undefined || DRAWS.includes(move['draw'] as string)) &&
      (move['card'] === undefined || isCard(move['card'])) &&
      (move['reshuffled'] === undefined || typeof move['reshuffled'] === 'boolean') &&
      isOptional(move['play'], (play) => isPlay(play, tiles.length)));
  const validPhase = state['phase'] === undefined || state['phase'] === 'Move' || state['phase'] === 'Play';
  const validWinner = state['winner'] === undefined || isIndex(state['winner']);
  const validCards =
    ['maxHand', 'deck', 'discard'].every((count) => state[count] === undefined || isIndex(state[count])) &&
    ['hand', 'playable'].every((cards) => isOptional(state[cards], (list) => Array.isArray(list) && list.every(isCard))) &&
    isOptional(state['cardValues'], isCardValues);
  if (tiles.length === 0 || !validTiles || !validPawns || !validMove || !validPhase || !validWinner || !validCards) {
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
