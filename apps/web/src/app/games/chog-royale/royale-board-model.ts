/** Chog Royale's round action (DEC-083): a card from the hand, with a target for a targeted card, or HODL; optionally locked. */
export const ROYALE_SUBMIT = 'SUBMIT_ACTION';

export type RoyaleCard = 'Pump' | 'RugPull' | 'DiamondHands' | 'WhaleSwap' | 'Airdrop' | 'Chaos';

export type RoyaleMarketEvent = 'CalmMarket' | 'MemeHype' | 'MarketCrash' | 'WhaleIncoming' | 'CommunityAirdrop';

/** What became of a played card (DEC-086, DEC-087, DEC-091). Only `Applied` counts as a successful card. */
export type RoyaleOutcome = 'Applied' | 'Blocked' | 'TargetLeft' | 'Taken' | 'Empty' | 'Stopped';

const CARDS: readonly RoyaleCard[] = ['Pump', 'RugPull', 'DiamondHands', 'WhaleSwap', 'Airdrop', 'Chaos'];
const EVENTS: readonly RoyaleMarketEvent[] = ['CalmMarket', 'MemeHype', 'MarketCrash', 'WhaleIncoming', 'CommunityAirdrop'];
const OUTCOMES: readonly RoyaleOutcome[] = ['Applied', 'Blocked', 'TargetLeft', 'Taken', 'Empty', 'Stopped'];

/** A card's name, what it does under rules version 2 (DEC-090) and whether it needs a target, as the hand shows it. */
export const CARD_INFO: Record<RoyaleCard, { name: string; text: string; targeted: boolean; icon: string }> = {
  Pump: { name: 'Pump', text: 'Gain 20 MON (more under Meme Hype, less in a Crash).', targeted: false, icon: '📈' },
  RugPull: { name: 'Rug Pull', text: 'Steal up to 15 MON from a player.', targeted: true, icon: '🪤' },
  DiamondHands: { name: 'Diamond Hands', text: 'Block the first attack on you this round and gain 5 MON.', targeted: false, icon: '💎' },
  WhaleSwap: { name: 'Whale Swap', text: 'Swap 20% of your MON with a player’s, up to 20 each way, and gain 10 MON.', targeted: true, icon: '🐋' },
  Airdrop: { name: 'Airdrop', text: 'Gain 5–25 MON at random.', targeted: false, icon: '🪂' },
  Chaos: { name: 'Chaos', text: 'Gain 10 MON and add a random market modifier for everyone this round.', targeted: false, icon: '🌀' },
};

/** A market event's name and what it does this round (ROYALE-021–025, DEC-088). */
export const EVENT_INFO: Record<RoyaleMarketEvent, { name: string; text: string; icon: string }> = {
  CalmMarket: { name: 'Calm Market', text: 'Nothing special this round.', icon: '🌤️' },
  MemeHype: { name: 'Meme Hype', text: 'Pumps gain 50% more.', icon: '🚀' },
  MarketCrash: { name: 'Market Crash', text: 'Pumps gain half as much.', icon: '📉' },
  WhaleIncoming: { name: 'Whale Incoming', text: 'A successful Rug Pull steals 5 MON more.', icon: '🐳' },
  CommunityAirdrop: { name: 'Community Airdrop', text: 'Everyone gains 5 MON after the cards.', icon: '🎁' },
};

/** The abilities' names, for the reveal (STORY-080, DEC-091). */
export const ABILITY_NAMES: Record<string, string> = {
  diamond_hands_plus: 'Diamond Hands+',
  alpha_leak: 'Alpha Leak',
  double_pump: 'Double Pump',
  stop_loss: 'Stop Loss',
};

export interface RoyaleHandCard {
  id: string;
  card: RoyaleCard;
}

/** Your pending action this round, only ever your own (DEC-083). */
export interface RoyaleAction {
  hodl: boolean;
  card: string | null;
  target: number | null;
}

/** A player still playing, as everyone sees them: never their cards, only how many. */
export interface RoyalePlayer {
  seat: number;
  mon: number;
  cards: number;
}

/** One player's line of a resolved round (DEC-085–091). */
export interface RoyaleLedgerEntry {
  seat: number;
  card: RoyaleCard | null;
  target: number | null;
  byDefault: boolean;
  outcome: RoyaleOutcome | null;
  amount: number;
  defense: 'Raised' | 'Blocked' | null;
  lost: number;
  market: number;
  change: number;
  balance: number;
  ability: string | null;
}

/** A resolved round: its event, the modifier Chaos added, the seat priority it resolved in and every player's line. */
export interface RoyaleRound {
  round: number;
  event: RoyaleMarketEvent;
  chaos: RoyaleMarketEvent | null;
  priority: number[];
  entries: RoyaleLedgerEntry[];
}

/** What this player may see of a Chog Royale match (DEC-083, DEC-091). */
export interface RoyaleBoard {
  rulesVersion: number;
  round: number;
  rounds: number;
  event: RoyaleMarketEvent;
  players: RoyalePlayer[];
  hand: RoyaleHandCard[];
  action: RoyaleAction | null;
  ledger: RoyaleRound[];
  /** The ability you used this round, until it resolves. */
  ability: string | null;
  /** The next round's event, only when you used Alpha Leak this round. */
  nextEvent: RoyaleMarketEvent | null;
}

type Json = Record<string, unknown>;

const isObject = (value: unknown): value is Json => typeof value === 'object' && value !== null && !Array.isArray(value);
const isInt = (value: unknown): value is number => typeof value === 'number' && Number.isInteger(value);
const isCount = (value: unknown): value is number => isInt(value) && value >= 0;
const isSeat = (value: unknown): value is number => isInt(value) && value >= 1;
const optional = <T>(value: unknown, check: (value: unknown) => value is T): T | null | undefined =>
  value === undefined || value === null ? null : check(value) ? value : undefined;
const isCard = (value: unknown): value is RoyaleCard => CARDS.includes(value as RoyaleCard);
const isEvent = (value: unknown): value is RoyaleMarketEvent => EVENTS.includes(value as RoyaleMarketEvent);
const isOutcome = (value: unknown): value is RoyaleOutcome => OUTCOMES.includes(value as RoyaleOutcome);
const isString = (value: unknown): value is string => typeof value === 'string' && value.length > 0;
const isDefense = (value: unknown): value is 'Raised' | 'Blocked' => value === 'Raised' || value === 'Blocked';

function toEntry(value: unknown): RoyaleLedgerEntry | null {
  if (!isObject(value)) {
    return null;
  }
  const card = optional(value['card'], isCard);
  const target = optional(value['target'], isSeat);
  const outcome = optional(value['outcome'], isOutcome);
  const defense = optional(value['defense'], isDefense);
  const ability = optional(value['ability'], isString);
  if (
    !isSeat(value['seat']) ||
    card === undefined ||
    target === undefined ||
    outcome === undefined ||
    defense === undefined ||
    ability === undefined ||
    typeof value['byDefault'] !== 'boolean' ||
    !isInt(value['amount']) ||
    !isCount(value['lost']) ||
    !isCount(value['market']) ||
    !isInt(value['change']) ||
    !isCount(value['balance'])
  ) {
    return null;
  }
  return {
    seat: value['seat'],
    card,
    target,
    byDefault: value['byDefault'],
    outcome,
    amount: value['amount'],
    defense,
    lost: value['lost'],
    market: value['market'],
    change: value['change'],
    balance: value['balance'],
    ability,
  };
}

function toRound(value: unknown): RoyaleRound | null {
  if (!isObject(value) || !isSeat(value['round']) || !isEvent(value['event']) || !Array.isArray(value['priority']) || !Array.isArray(value['entries'])) {
    return null;
  }
  const chaos = optional(value['chaos'], isEvent);
  const entries = value['entries'].map(toEntry);
  if (chaos === undefined || !value['priority'].every(isSeat) || entries.some((entry) => entry === null)) {
    return null;
  }
  return { round: value['round'], event: value['event'], chaos, priority: value['priority'] as number[], entries: entries as RoyaleLedgerEntry[] };
}

function toAction(value: unknown): RoyaleAction | null | undefined {
  if (value === undefined || value === null) {
    return null;
  }
  if (!isObject(value)) {
    return undefined;
  }
  const card = optional(value['card'], isString);
  const target = optional(value['target'], isSeat);
  return card === undefined || target === undefined ? undefined : { hodl: value['hodl'] === true, card, target };
}

/** The Chog Royale view in a room's `state`, or null when it isn't one this client understands. */
export function toRoyaleBoard(state: unknown): RoyaleBoard | null {
  if (!isObject(state) || !isSeat(state['rulesVersion']) || !isSeat(state['round']) || !isSeat(state['rounds']) || !isEvent(state['event'])) {
    return null;
  }
  const players = Array.isArray(state['players']) ? state['players'] : null;
  const hand = Array.isArray(state['hand']) ? state['hand'] : null;
  const ledger = Array.isArray(state['ledger']) ? state['ledger'].map(toRound) : null;
  const action = toAction(state['action']);
  const ability = optional(state['ability'], isString);
  const nextEvent = optional(state['nextEvent'], isEvent);
  if (
    !players?.every((p) => isObject(p) && isSeat(p['seat']) && isCount(p['mon']) && isCount(p['cards'])) ||
    !hand?.every((c) => isObject(c) && isString(c['id']) && isCard(c['card'])) ||
    !ledger ||
    ledger.some((round) => round === null) ||
    action === undefined ||
    ability === undefined ||
    nextEvent === undefined
  ) {
    return null;
  }
  return {
    rulesVersion: state['rulesVersion'],
    round: state['round'],
    rounds: state['rounds'],
    event: state['event'],
    players: players.map((p) => ({ seat: p['seat'], mon: p['mon'], cards: p['cards'] }) as RoyalePlayer),
    hand: hand.map((c) => ({ id: c['id'], card: c['card'] }) as RoyaleHandCard),
    action,
    ledger: ledger as RoyaleRound[],
    ability,
    nextEvent,
  };
}

/** "+20 MON", "−15 MON", "±0 MON". */
export function signedMon(change: number): string {
  return change > 0 ? `+${change} MON` : change < 0 ? `−${-change} MON` : '±0 MON';
}

/**
 * A plain-language line for one player's part in a round's reveal (ROYALE-049, ROYALE-051): what they played and at
 * whom, what became of it, their defense and the market, ending with their MON change. `name` names a seat.
 */
export function describeEntry(entry: RoyaleLedgerEntry, name: (seat: number) => string): string {
  const parts: string[] = [];
  if (entry.ability) {
    parts.push(`used ${ABILITY_NAMES[entry.ability] ?? entry.ability}`);
  }
  if (!entry.card) {
    parts.push(entry.byDefault ? 'didn’t act, so held (HODL)' : 'held (HODL)');
  } else {
    const card = CARD_INFO[entry.card].name;
    const at = entry.target !== null ? ` at ${name(entry.target)}` : '';
    parts.push(`played ${card}${at}${outcomeText(entry)}`);
  }
  if (entry.defense === 'Blocked') {
    parts.push('blocked an attack');
  }
  if (entry.lost > 0) {
    parts.push(`lost ${entry.lost} MON to attacks`);
  }
  if (entry.market > 0) {
    parts.push(`got ${entry.market} MON from the market`);
  }
  return `${name(entry.seat)} ${joinParts(parts)}: ${signedMon(entry.change)}, now ${entry.balance} MON.`;
}

function outcomeText(entry: RoyaleLedgerEntry): string {
  switch (entry.outcome) {
    case 'Applied':
      if (entry.card === 'WhaleSwap') {
        return ` and swapped (${signedMon(entry.amount)})`;
      }
      if (entry.card === 'RugPull') {
        return ` and stole ${entry.amount} MON`;
      }
      if (entry.card === 'Chaos') {
        return entry.amount > 0 ? ` and shook the market (+${entry.amount} MON)` : ' and shook the market';
      }
      return entry.amount > 0 ? ` (+${entry.amount} MON)` : '';
    case 'Blocked':
      return ', but it was blocked';
    case 'TargetLeft':
      return ', but they had left';
    case 'Taken':
      return entry.card === 'Chaos' ? (entry.amount > 0 ? `, after the market had already shifted (+${entry.amount} MON)` : ', after the market had already shifted') : ', but a swap had already happened';
    case 'Empty':
      return ', but they had nothing left';
    case 'Stopped':
      return ', but Stop Loss stopped it';
    default:
      return '';
  }
}

function joinParts(parts: string[]): string {
  return parts.length > 1 ? `${parts.slice(0, -1).join(', ')} and ${parts[parts.length - 1]}` : parts[0];
}
