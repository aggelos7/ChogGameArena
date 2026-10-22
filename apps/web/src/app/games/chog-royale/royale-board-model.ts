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

/** The abilities' names, for the reveal (STORY-080, STORY-093, DEC-091, DEC-095). */
export const ABILITY_NAMES: Record<string, string> = {
  diamond_hands_plus: 'Diamond Hands+',
  alpha_leak: 'Alpha Leak',
  double_pump: 'Double Pump',
  stop_loss: 'Stop Loss',
  fresh_mint: 'Fresh Mint',
  insider_info: 'Insider Info',
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
  /** The seat whose hand the player's Insider Info showed them (STORY-093, DEC-095). */
  abilityTarget: number | null;
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
  /** The opponent your Insider Info looks at this round and their hand as it is now, never their action (DEC-095). */
  insider: RoyaleInsider | null;
}

export interface RoyaleInsider {
  seat: number;
  hand: RoyaleHandCard[];
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
  const abilityTarget = optional(value['abilityTarget'], isSeat);
  if (
    !isSeat(value['seat']) ||
    card === undefined ||
    target === undefined ||
    outcome === undefined ||
    defense === undefined ||
    ability === undefined ||
    abilityTarget === undefined ||
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
    abilityTarget,
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

const isHand = (value: unknown): value is RoyaleHandCard[] =>
  Array.isArray(value) && value.every((c) => isObject(c) && isString(c['id']) && isCard(c['card']));

function toInsider(value: unknown): RoyaleInsider | null | undefined {
  if (value === undefined || value === null) {
    return null;
  }
  return isObject(value) && isSeat(value['seat']) && isHand(value['hand'])
    ? { seat: value['seat'], hand: value['hand'].map((c) => ({ id: c.id, card: c.card })) }
    : undefined;
}

/** The Chog Royale view in a room's `state`, or null when it isn't one this client understands. */
export function toRoyaleBoard(state: unknown): RoyaleBoard | null {
  if (!isObject(state) || !isSeat(state['rulesVersion']) || !isSeat(state['round']) || !isSeat(state['rounds']) || !isEvent(state['event'])) {
    return null;
  }
  const players = Array.isArray(state['players']) ? state['players'] : null;
  const hand = isHand(state['hand']) ? state['hand'] : null;
  const ledger = Array.isArray(state['ledger']) ? state['ledger'].map(toRound) : null;
  const action = toAction(state['action']);
  const ability = optional(state['ability'], isString);
  const nextEvent = optional(state['nextEvent'], isEvent);
  const insider = toInsider(state['insider']);
  if (
    !players?.every((p) => isObject(p) && isSeat(p['seat']) && isCount(p['mon']) && isCount(p['cards'])) ||
    !hand ||
    !ledger ||
    ledger.some((round) => round === null) ||
    action === undefined ||
    ability === undefined ||
    nextEvent === undefined ||
    insider === undefined
  ) {
    return null;
  }
  return {
    rulesVersion: state['rulesVersion'],
    round: state['round'],
    rounds: state['rounds'],
    event: state['event'],
    players: players.map((p) => ({ seat: p['seat'], mon: p['mon'], cards: p['cards'] }) as RoyalePlayer),
    hand: hand.map((c) => ({ id: c.id, card: c.card })),
    action,
    ledger: ledger as RoyaleRound[],
    ability,
    nextEvent,
    insider,
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
    const on = entry.abilityTarget !== null ? ` on ${name(entry.abilityTarget)}` : '';
    parts.push(`used ${ABILITY_NAMES[entry.ability] ?? entry.ability}${on}`);
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

/** Each player's part of the table's reveal starts this long after the previous one's (STORY-095): six settle in 2.8 s. */
export const REVEAL_STEP_MS = 420;

/** A card takes this long to fly to its target, and a balance as long to count to the server's (STORY-095). */
export const REVEAL_MOVE_MS = 350;

/** A card's colour on the table, so cards tell apart at a glance; the name and icon say the same. */
export const CARD_ACCENT: Record<RoyaleCard | 'Hodl', string> = {
  Pump: '#13703a',
  RugPull: '#b3261e',
  DiamondHands: '#0a6c8f',
  WhaleSwap: '#1d4fbf',
  Airdrop: '#b4400b',
  Chaos: '#7b2fbe',
  Hodl: '#5c5470',
};

/** What a played card did where it landed, shown on the target's seat (DEC-086, DEC-087, DEC-091). */
const IMPACT: Record<RoyaleOutcome, string> = {
  Applied: 'Hit',
  Blocked: '🛡️ Blocked',
  TargetLeft: 'Left',
  Taken: 'Too late',
  Empty: 'Empty',
  Stopped: 'Stop Loss',
};

/** One player's part of a round's reveal on the table, in the order the round resolved (STORY-095, DEC-085). */
export interface RevealStep {
  seat: number;
  /** When it starts, in milliseconds from the reveal's start. */
  at: number;
  /** The card played, or null for HODL. */
  card: RoyaleCard | null;
  /** The seat a targeted card flies to, and what it did there. */
  target: number | null;
  impact: string | null;
  /** The ability used, by name, and the seat it was used on. */
  ability: string | null;
  abilityTarget: number | null;
  /** The player's balance before the round and the server's after it. */
  from: number;
  to: number;
  change: number;
  /** False for a line this client doesn't know how to draw: it shows at once, as its text line does. */
  animated: boolean;
}

/**
 * A round's reveal on the table (STORY-095): one step per ledger entry in resolution order, `REVEAL_STEP_MS` apart. It
 * replays only what the ledger says; an entry with an unknown card, outcome or ability isn't animated.
 */
export function revealSteps(round: RoyaleRound): RevealStep[] {
  const order = (seat: number) => {
    const index = round.priority.indexOf(seat);
    return index < 0 ? Number.MAX_SAFE_INTEGER : index;
  };
  let next = 0;
  return [...round.entries]
    .sort((a, b) => order(a.seat) - order(b.seat))
    .map((entry) => {
      const animated =
        (entry.card === null || entry.card in CARD_INFO) &&
        (entry.outcome === null || entry.outcome in IMPACT) &&
        (entry.ability === null || entry.ability in ABILITY_NAMES);
      const at = animated ? next : 0;
      if (animated) {
        next += REVEAL_STEP_MS;
      }
      const targeted = animated && entry.card !== null && CARD_INFO[entry.card].targeted && entry.target !== null;
      return {
        seat: entry.seat,
        at,
        card: entry.card,
        target: targeted ? entry.target : null,
        impact: targeted && entry.outcome ? IMPACT[entry.outcome] : null,
        ability: entry.ability ? (ABILITY_NAMES[entry.ability] ?? entry.ability) : null,
        abilityTarget: entry.abilityTarget,
        from: entry.balance - entry.change,
        to: entry.balance,
        change: entry.change,
        animated,
      };
    });
}

/** How long a reveal takes from its first step to every balance settled; 0 with nothing to animate. */
export function revealDuration(steps: RevealStep[]): number {
  const last = Math.max(-1, ...steps.filter((step) => step.animated).map((step) => step.at));
  return last < 0 ? 0 : last + 2 * REVEAL_MOVE_MS;
}

/**
 * A step `elapsed` milliseconds into the reveal: whether its card is face up yet, whether the card is still in the air
 * (`flying`) or has landed, and the balance shown, counting from the old to the server's once the card has landed.
 */
export function stepAt(step: RevealStep, elapsed: number): { shown: boolean; flying: boolean; landed: boolean; mon: number } {
  const t = step.animated ? elapsed - step.at : Number.POSITIVE_INFINITY;
  const counted = Math.min(1, Math.max(0, (t - REVEAL_MOVE_MS) / REVEAL_MOVE_MS));
  return {
    shown: t >= 0,
    flying: step.target !== null && t >= 0 && t < REVEAL_MOVE_MS,
    landed: t >= REVEAL_MOVE_MS,
    mon: Math.round(step.from + (step.to - step.from) * counted),
  };
}
