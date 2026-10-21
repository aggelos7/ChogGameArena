/** Mirrors the server's tile kinds (DEC-054). */
export type RaceTileType = 'Start' | 'Normal' | 'Card' | 'Boost' | 'Setback' | 'Finish';

/** A tile as the server shows it: the track runs by index; a Boost or Setback sends a pawn that lands on it `to` a tile. */
export interface RaceTile {
  index: number;
  type: RaceTileType;
  to?: number;
}

/**
 * Mirrors the server's cards (RACE-005, DEC-056). Every card but HODL is played on your turn (STORY-053, DEC-057;
 * STORY-054, DEC-058); HODL only in reply to a Rug, Liquidated or Whale aimed at you.
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
  /** The tiles an armed Speed Boost adds to the player's next roll (STORY-055, DEC-059). */
  boost?: number;
  /** A raised Shield, blocking the next Rug, Liquidated or Whale played on the player (DEC-059). */
  shielded?: boolean;
  /** How many more attacks a raised Shield blocks, from the player's Tier (STORY-090, DEC-077). */
  blocks?: number;
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
  /** The tiles Speed Boost added to the roll (STORY-055, DEC-059). */
  boost?: number;
}

/**
 * An opponent Reverse or Rug moved back: the tile it `landed` on, and `to` after a Boost or Setback there (the `effect`)
 * sent it on. Ending on a Card tile draws there (revised in STORY-055): the `card` is sent only to that pawn's player.
 */
export interface RacePush {
  seat: number;
  from: number;
  landed: number;
  to: number;
  effect?: 'Boost' | 'Setback';
  draw?: RaceDraw;
  card?: RaceCard;
  reshuffled?: boolean;
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
  /** How the target of Rug, Liquidated or Whale answered it (DEC-058); absent while they're deciding or had no cards. */
  reply?: RaceReply;
}

/**
 * How an attack's target answered (DEC-058): blocked with HODL, let it land, or didn't answer in time; or their Shield
 * blocked it with no reply window (DEC-059).
 */
export type RaceReply = 'Blocked' | 'Allowed' | 'TimedOut' | 'Shielded';

/**
 * The latest ability used (STORY-055, DEC-059): who used which, and for Card Steal from whom and, only for those two
 * players, the card.
 */
export interface RaceAbilityUse {
  seat: number;
  ability: string;
  target?: number;
  card?: RaceCard;
  /** Every card Card Steal took, for those two players (STORY-090, DEC-077). */
  cards?: RaceCard[];
  /** How many cards Card Steal took, for everyone; absent before STORY-090 (one). */
  taken?: number;
}

/** An attack waiting for its target's reply (DEC-058): the attacker's seat, the card and the target's seat. */
export interface RaceAttack {
  seat: number;
  card: RaceCard;
  target: number;
}

/** How far the cards move pawns, from the match's rules version (DEC-057). */
export interface RaceCardValues {
  pump: number;
  fomoForward: number;
  fomoBack: number;
  fomoForwardChance: number;
  reverse: number;
  /** How far Rug moves its target back (DEC-058). */
  rug: number;
}

/**
 * The current player's phase (DEC-055): rolling, or playing an optional card before ending the turn; `Reply` while their
 * attack waits for its target's answer (DEC-058).
 */
export type RacePhase = 'Move' | 'Play' | 'Reply';

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
  /** The attack waiting for its target's reply, in the `Reply` phase (DEC-058). */
  reply?: RaceAttack;
  /** The seats due to skip their next turn after a Liquidated (DEC-058). */
  skipping?: number[];
  /** The deck's top card, sent only to the player who saw it with Card Vision, until it's drawn (DEC-059). */
  next?: RaceCard;
  /** Every top card Card Vision shows its player, top first: one, or more from a rarer Tier (STORY-090, DEC-077). */
  seen?: RaceCard[];
  /** The latest ability used, until another player rolls or uses one (DEC-059). */
  ability?: RaceAbilityUse;
  /** How the race ended, once it's over: a pawn reached the Finish, or the 20-minute cap (STORY-056, DEC-060). */
  ended?: RaceEnding;
}

/** How a race ended (DEC-060). */
export type RaceEnding = 'Finish' | 'TimeUp';

/** Rolls, moves and resolves the tile landed on, in the Move phase (DEC-055). */
export const RACE_ROLL = 'ROLL';

/** Ends the turn in the Play phase, without playing a card (DEC-055). */
export const RACE_END_TURN = 'END_TURN';

/** Plays a card in the Play phase, which ends the turn (DEC-057), or HODL in reply to an attack (DEC-058). */
export const RACE_PLAY_CARD = 'PLAY_CARD';

/** Lets an attack on you land instead of blocking it (DEC-058). */
export const RACE_PASS = 'PASS';

/** Chog Race's abilities (STORY-055, DEC-059): only Speed Boost is limited to before the roll; Card Steal takes a target. */
export const RACE_SPEED_BOOST = 'movement_boost';
export const RACE_SHIELD = 'attack_block';
export const RACE_CARD_VISION = 'card_preview';
export const RACE_CARD_STEAL = 'card_steal';

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
      return values ? `Move an opponent ${values.rug} back.` : 'Move an opponent back.';
    case 'Hodl':
      return 'Block a Rug, Liquidated or Whale played on you.';
    case 'Liquidated':
      return 'An opponent skips their next turn.';
  }
}

/**
 * Where a tile sits on the drawn track: its grid row and column, from 1, and which way the arrow to the next tile points;
 * null on the Finish.
 */
export interface RaceSpot {
  tile: RaceTile;
  row: number;
  column: number;
  next: 'right' | 'left' | 'down' | null;
}

/** The fewest and most tiles a row of the drawn track holds; 4 fits a 375px phone (DEC-054). */
export const MIN_COLUMNS = 4;
export const MAX_COLUMNS = 10;

/** The least width a tile is drawn at, in pixels, so pawns and labels stay legible and tappable. */
export const TILE_WIDTH = 84;

/** The gap between tiles, in pixels, where the arrow to the next tile is drawn; wider beside the players (race-board.scss). */
export const TRACK_GAP = 20;
export const FIT_GAP = 40;

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
const REPLIES: readonly string[] = ['Blocked', 'Allowed', 'TimedOut', 'Shielded'];

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
    isOptional(play['reply'], (reply) => REPLIES.includes(reply as string)) &&
    isOptional(
      play['pushed'],
      (pushed) =>
        Array.isArray(pushed) &&
        pushed.every(
          (push) =>
            isRecord(push) &&
            isIndex(push['seat']) &&
            ['from', 'landed', 'to'].every((key) => isTile(push[key])) &&
            isOptional(push['effect'], (effect) => effect === 'Boost' || effect === 'Setback') &&
            isOptional(push['draw'], (draw) => DRAWS.includes(draw as string)) &&
            isOptional(push['card'], isCard) &&
            isOptional(push['reshuffled'], (reshuffled) => typeof reshuffled === 'boolean'),
        ),
    )
  );
}

const isCardValues = (values: unknown): boolean =>
  isRecord(values) && ['pump', 'fomoForward', 'fomoBack', 'fomoForwardChance', 'reverse', 'rug'].every((key) => isIndex(values[key]));

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
      (pawn['cards'] === undefined || isIndex(pawn['cards'])) &&
      isOptional(pawn['boost'], isIndex) &&
      isOptional(pawn['shielded'], (shielded) => typeof shielded === 'boolean') &&
      isOptional(pawn['blocks'], isIndex),
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
      isOptional(move['boost'], isIndex) &&
      isOptional(move['play'], (play) => isPlay(play, tiles.length)));
  const validPhase = state['phase'] === undefined || ['Move', 'Play', 'Reply'].includes(state['phase'] as string);
  const reply = state['reply'];
  const validReply =
    reply === undefined || (isRecord(reply) && isIndex(reply['seat']) && isCard(reply['card']) && isIndex(reply['target']));
  const validSkipping = isOptional(state['skipping'], (seats) => Array.isArray(seats) && seats.every(isIndex));
  const validWinner =
    (state['winner'] === undefined || isIndex(state['winner'])) &&
    isOptional(state['ended'], (ended) => ended === 'Finish' || ended === 'TimeUp');
  const ability = state['ability'];
  const validAbility =
    isOptional(state['next'], isCard) &&
    isOptional(state['seen'], (cards) => Array.isArray(cards) && cards.every(isCard)) &&
    isOptional(
      ability,
      (used) =>
        isRecord(used) &&
        isIndex(used['seat']) &&
        typeof used['ability'] === 'string' &&
        isOptional(used['target'], isIndex) &&
        isOptional(used['card'], isCard) &&
        isOptional(used['cards'], (cards) => Array.isArray(cards) && cards.every(isCard)) &&
        isOptional(used['taken'], isIndex),
    );
  const validCards =
    ['maxHand', 'deck', 'discard'].every((count) => state[count] === undefined || isIndex(state[count])) &&
    ['hand', 'playable'].every((cards) => isOptional(state[cards], (list) => Array.isArray(list) && list.every(isCard))) &&
    isOptional(state['cardValues'], isCardValues);
  if (tiles.length === 0 || !validTiles || !validPawns || !validMove || !validPhase || !validWinner || !validCards || !validReply || !validSkipping || !validAbility) {
    return null;
  }
  return state as unknown as RaceBoard;
}

/** How many tiles a row holds on a board `width` pixels wide. */
export function raceColumns(width: number): number {
  return Math.min(MAX_COLUMNS, Math.max(MIN_COLUMNS, Math.floor((width + TRACK_GAP) / (TILE_WIDTH + TRACK_GAP))));
}

/**
 * How many tiles a row holds when `count` tiles fill a box `width` by `height` pixels, beside the players: the number
 * whose tiles come out largest, so the track is as near square tiles as the box allows.
 */
export function raceFitColumns(count: number, width: number, height: number): number {
  let best = MIN_COLUMNS;
  let bestSide = 0;
  for (let columns = MIN_COLUMNS; columns <= MAX_COLUMNS; columns++) {
    const rows = Math.ceil(count / columns);
    const side = Math.min((width - (columns - 1) * FIT_GAP) / columns, (height - (rows - 1) * FIT_GAP) / rows);
    if (side > bestSide) {
      best = columns;
      bestSide = side;
    }
  }
  return best;
}

/**
 * The track laid out as a serpentine in rows of `columns` tiles: the first row left to right, the next right to left, and
 * so on, so consecutive tiles always touch and the whole race fits on a phone screen. Each tile's arrow points along the
 * row, or down at its end.
 */
export function raceLayout(tiles: RaceTile[], columns: number): RaceSpot[] {
  return tiles.map((tile, i) => {
    const row = Math.floor(i / columns);
    const step = i % columns;
    const forward = row % 2 === 0 ? 'right' : 'left';
    const next = i === tiles.length - 1 ? null : step === columns - 1 ? 'down' : forward;
    return { tile, row: row + 1, column: (row % 2 === 0 ? step : columns - 1 - step) + 1, next };
  });
}

/**
 * The tiles a pawn passes, one at a time, going `from` a tile to where it `landed` and on `to` where a Boost or Setback
 * there sent it: the tiles it's shown on as it hops, `from` first.
 */
export function raceWalk(from: number, landed: number, to: number): number[] {
  const tiles = [from];
  for (const stop of [landed, to]) {
    let at = tiles[tiles.length - 1];
    while (at !== stop) {
      at += Math.sign(stop - at);
      tiles.push(at);
    }
  }
  return tiles;
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
