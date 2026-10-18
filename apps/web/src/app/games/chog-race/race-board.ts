import {
  Component,
  DestroyRef,
  ElementRef,
  afterEveryRender,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { RoomMember } from '../../rooms/rooms-api';
import { HOP_MS, SIDE_BY_SIDE, reducedMotion } from '../chog-life/life-board';
import { timeLeft } from '../chog-life/life-board-model';
import { seatColor } from '../seats';
import {
  RACE_CARD_NAMES,
  RACE_CARD_STEAL,
  RACE_CARD_VISION,
  RACE_SHIELD,
  RACE_SPEED_BOOST,
  RaceBoard,
  RaceCard,
  RaceDraw,
  RaceMove,
  RacePlay,
  RacePush,
  RaceTile,
  raceCardDescription,
  raceColumns,
  raceFitColumns,
  raceLayout,
  raceTileLabel,
  raceWalk,
} from './race-board-model';

/** A card you can play now, and for Whale, Rug and Liquidated the opponent it's played on (DEC-057, DEC-058). */
export interface RaceCardPlay {
  card: RaceCard;
  target?: number;
}

/** Your locked ability used now, and for Card Steal the opponent it's used on (STORY-055, DEC-059). */
export interface RaceAbilityPlay {
  abilityId: string;
  target?: number;
}

/** Where a pawn's own move ended and what the tile did, for a roll or a card's move alike. */
type Landing = Pick<RaceMove, 'landed' | 'to' | 'effect' | 'draw' | 'reshuffled'> & { card?: RaceCard };

/** The least height the track is given beside the players, however little is left on screen. */
const LEAST_HEIGHT = 360;

/** A pawn as drawn: its seat, colour and the player it belongs to. */
interface DrawnPawn {
  seat: number;
  color: string;
  /** The player's profile photo, drawn as the pawn inside a ring of the seat colour (STORY-062); null for a numbered pawn. */
  imageUrl: string | null;
  name: string;
  isYou: boolean;
  isTurn: boolean;
  /** The pawn is hopping along a move being shown. */
  isHopping?: boolean;
}

/**
 * The Chog Race board (STORY-050, DEC-054): the track as a serpentine grid, four tiles a row on a phone and up to ten on
 * wider screens, so the whole race is on screen. Each tile shows its number, what it does and the pawns on it, up to all
 * six. Pawns are numbered and coloured by seat, yours outlined and the current player's ringed, and the legend names
 * each player and whose turn it is. It only draws what the server sent. On your turn it offers Roll in the Move phase and
 * End turn in the Play phase (STORY-051, DEC-055), and says what the latest move did; the server decides every outcome.
 * It shows your hand, how many cards each player holds and what's left in the deck and discard pile (STORY-052, DEC-056);
 * the server only ever sends your own cards. In the Play phase it offers a button for each card you can play, Whale once
 * per opponent, and says what the card did (STORY-053, DEC-057). Rug and Liquidated are offered once per opponent they
 * can hurt; when one, or a Whale, is played on you while you hold cards, it asks you to block it with HODL or take it, and
 * everyone sees who it waits for and who skips a turn (STORY-054, DEC-058). On your turn it offers your locked ability
 * while it has a use left and fits the moment (STORY-055, DEC-059): Speed Boost before the roll, Shield and Card Vision
 * before or after it, and Card Steal once per opponent holding a card while your hand has room. Everyone sees armed
 * Speed Boosts, raised Shields and the latest ability used; only you see the card Card Vision shows you, and a stolen
 * card only its two players. While the race runs it counts down to the 20-minute cap (STORY-056, DEC-060). On wide
 * screens it's laid out like the Chog Life board (DEC-048): the players and controls in a column on the left, and the
 * track on the right, filling the height left on screen. Each new move is shown as on the Chog Life board: the pawn hops
 * a tile at a time to where it landed and on where a Boost or Setback sent it, then any pawns a card pushed back hop too.
 */
@Component({
  selector: 'app-race-board',
  templateUrl: './race-board.html',
  styleUrl: './race-board.scss',
})
export class RaceBoardView {
  readonly board = input.required<RaceBoard>();
  readonly members = input.required<RoomMember[]>();
  /** When the match's time is up (ISO 8601), counted down while it runs; null for no clock. */
  readonly endsAt = input<string | null>(null);
  /** A command is on its way: the buttons wait for it. */
  readonly rolling = input(false);
  readonly roll = output<void>();
  readonly endTurn = output<void>();
  readonly playCard = output<RaceCardPlay>();
  /** Lets an attack on you land (DEC-058). */
  readonly pass = output<void>();
  /** Uses your locked ability (DEC-059). */
  readonly useAbility = output<RaceAbilityPlay>();

  protected readonly label = raceTileLabel;

  /** The time now, ticking every second for the clock. */
  private readonly now = signal(Date.now());

  /** The time left to the cap, while the race runs; null once it's over or without a cap. */
  protected readonly clock = computed(() => {
    const endsAt = this.endsAt();
    const end = endsAt ? Date.parse(endsAt) : NaN;
    return Number.isNaN(end) || this.board().ended || this.board().winner !== undefined ? null : timeLeft(end, this.now());
  });

  /** The board's width, once measured; until then, and without ResizeObserver, the phone layout. */
  private readonly width = signal(0);

  /** Beside the players: the track's width and the height left for it on screen, in pixels; null otherwise. */
  protected readonly space = signal<{ width: number; height: number } | null>(null);
  private readonly track = viewChild<ElementRef<HTMLElement>>('track');

  protected readonly columns = computed(() => {
    const space = this.space();
    return space ? raceFitColumns(this.board().tiles.length, space.width, space.height) : raceColumns(this.width());
  });
  protected readonly rows = computed(() => Math.ceil(this.board().tiles.length / this.columns()));
  protected readonly spots = computed(() => raceLayout(this.board().tiles, this.columns()));

  /** Players with a pawn on the board, by seat. */
  protected readonly players = computed(() => {
    const onBoard = new Set(this.board().pawns.map((pawn) => pawn.seat));
    return this.members()
      .filter((member): member is RoomMember & { seat: number } => member.seat !== null && onBoard.has(member.seat))
      .sort((a, b) => a.seat - b.seat);
  });

  /** Where pawns are shown while a move hops along, by seat; null when no move is being shown. */
  private readonly hops = signal<ReadonlyMap<number, number> | null>(null);
  /** The latest roll and card play already seen; undefined before the first board, whose move is never replayed. */
  private seenRoll: string | null | undefined = undefined;
  private seenPlay: string | null | undefined = undefined;
  private hopTimer: ReturnType<typeof setTimeout> | undefined;

  /** The pawns standing on each tile, in seat order, where the server put them. */
  protected readonly pawnsByTile = computed(() => this.byTile(new Map()));

  /** The pawns as drawn on each tile: where the server put them, or where they are while a move hops along. */
  protected readonly shownByTile = computed(() => this.byTile(this.hops() ?? new Map()));

  private byTile(hops: ReadonlyMap<number, number>): Map<number, DrawnPawn[]> {
    const bySeat = new Map(this.players().map((member) => [member.seat, member]));
    const byTile = new Map<number, DrawnPawn[]>();
    for (const pawn of [...this.board().pawns].sort((a, b) => a.seat - b.seat)) {
      const member = bySeat.get(pawn.seat);
      const drawn: DrawnPawn = {
        seat: pawn.seat,
        color: seatColor(pawn.seat),
        imageUrl: member?.avatarImageUrl ?? null,
        name: member?.displayName ?? `Seat ${pawn.seat}`,
        isYou: !!member?.isYou,
        isTurn: !!member?.isTurn,
        isHopping: hops.has(pawn.seat),
      };
      const tile = hops.get(pawn.seat) ?? pawn.tile;
      byTile.set(tile, [...(byTile.get(tile) ?? []), drawn]);
    }
    return byTile;
  }

  /** How many cards each seat holds. */
  protected readonly cardCounts = computed(() => new Map(this.board().pawns.map((pawn) => [pawn.seat, pawn.cards ?? 0])));

  /** Your cards by name, while you're playing; null when the server sent no hand. */
  /** Your cards by name with what each does, while you're playing; null when the server sent no hand. */
  protected readonly hand = computed(
    () =>
      this.board().hand?.map((card) => ({ name: this.cardName(card), description: raceCardDescription(card, this.board().cardValues) })) ??
      null,
  );

  protected readonly turnOf = computed(() => this.players().find((member) => member.isTurn) ?? null);

  /** Seats due to skip their next turn (DEC-058). */
  protected readonly skipping = computed(() => new Set(this.board().skipping ?? []));

  /**
   * The attack waiting for its target's answer, in the Reply phase (DEC-058), in words: what happened, whether it's you
   * who answers, and whether you hold a HODL to block it with.
   */
  protected readonly waitingReply = computed(() => {
    const board = this.board();
    const attack = board.reply;
    if (board.phase !== 'Reply' || !attack) {
      return null;
    }
    const target = this.members().find((member) => member.seat === attack.target);
    const card = this.cardName(attack.card);
    const isYou = !!target?.isYou;
    const canBlock = isYou && (board.hand ?? []).includes('Hodl');
    return {
      isYou,
      canBlock,
      text: isYou
        ? `${this.nameOf(attack.seat)} played ${card} on you. ${canBlock ? 'Block it with HODL or take it.' : 'You have no HODL to block it.'}`
        : `Waiting for ${this.nameOf(attack.target, false)} to answer ${this.possessive(attack.seat)} ${card}.`,
    };
  });

  /** Your turn's phase, while it's your turn in a live match; null otherwise. */
  protected readonly yourPhase = computed(() => (this.turnOf()?.isYou ? (this.board().phase ?? null) : null));

  /** The cards you can play now, in your Play phase, with what each does; Whale once per opponent. */
  protected readonly plays = computed(() => {
    if (this.yourPhase() !== 'Play') {
      return [];
    }
    const values = this.board().cardValues;
    const opponents = this.board().pawns.filter((pawn) => pawn.seat !== this.turnOf()?.seat);
    return (this.board().playable ?? []).flatMap((card): (RaceCardPlay & { label: string })[] => {
      const name = this.cardName(card);
      switch (card) {
        case 'Rug':
          // A pawn on the Start has nowhere to go back to.
          return opponents
            .filter((pawn) => pawn.tile > 0)
            .map((pawn) => ({
              card,
              target: pawn.seat,
              label: `${name}: ${this.nameOf(pawn.seat, false)} ${values ? `${values.rug} ` : ''}back`,
            }));
        case 'Liquidated':
          // Skips don't stack.
          return opponents
            .filter((pawn) => !this.skipping().has(pawn.seat))
            .map((pawn) => ({ card, target: pawn.seat, label: `${name}: ${this.nameOf(pawn.seat, false)} skips a turn` }));
        case 'Whale':
          return opponents.map((pawn) => ({
            card,
            target: pawn.seat,
            label: `${name}: swap with ${this.nameOf(pawn.seat, false)} (tile ${pawn.tile})`,
          }));
        case 'Pump':
          return [{ card, label: values ? `${name}: ${values.pump} forward` : name }];
        case 'Fomo':
          return [{ card, label: values ? `${name}: ${values.fomoForward} forward or ${values.fomoBack} back` : name }];
        case 'Airdrop':
          return [{ card, label: `${name}: roll again` }];
        case 'Reverse':
          return [{ card, label: values ? `${name}: others ${values.reverse} back` : name }];
        default:
          return [{ card, label: name }];
      }
    });
  });

  /** Armed Speed Boosts and raised Shields, by seat (DEC-059). */
  protected readonly armed = computed(() => new Map(this.board().pawns.map((pawn) => [pawn.seat, pawn])));

  /**
   * Your locked ability's buttons, on your turn while it has a use left and fits the moment (DEC-059): Speed Boost before
   * the roll and not yet armed, Shield not yet raised, Card Vision with cards in the deck, and Card Steal once per
   * opponent holding a card while your hand has room. The server checks each again.
   */
  protected readonly abilityPlays = computed((): (RaceAbilityPlay & { label: string })[] => {
    const phase = this.yourPhase();
    const you = this.turnOf();
    const ability = you?.ability;
    const board = this.board();
    if ((phase !== 'Move' && phase !== 'Play') || !you || !ability?.usesLeft) {
      return [];
    }
    const pawn = this.armed().get(you.seat);
    switch (ability.id) {
      case RACE_SPEED_BOOST:
        return phase === 'Move' && !pawn?.boost ? [{ abilityId: ability.id, label: `Use ${ability.name}: +2 to your roll` }] : [];
      case RACE_SHIELD:
        return pawn?.shielded ? [] : [{ abilityId: ability.id, label: `Use ${ability.name}` }];
      case RACE_CARD_VISION:
        return (board.deck ?? 0) > 0 ? [{ abilityId: ability.id, label: `Use ${ability.name}: see the next card` }] : [];
      case RACE_CARD_STEAL:
        if ((board.hand?.length ?? 0) >= (board.maxHand ?? Infinity)) {
          return [];
        }
        return board.pawns
          .filter((other) => other.seat !== you.seat && (other.cards ?? 0) > 0)
          .map((other) => ({ abilityId: ability.id, target: other.seat, label: `${ability.name}: from ${this.nameOf(other.seat, false)}` }));
      default:
        return [{ abilityId: ability.id, label: `Use ${ability.name}` }];
    }
  });

  /**
   * Your locked ability, always shown while you're playing a live match (DEC-059): what it does, the uses left and, when it
   * can't be used now, why; its buttons go with it.
   */
  protected readonly yourAbility = computed(() => {
    const board = this.board();
    const you = this.players().find((member) => member.isYou);
    const ability = you?.ability;
    if (!you || !ability || ability.usesLeft === null || !board.phase) {
      return null;
    }
    return { ...ability, status: this.abilityPlays().length > 0 ? null : this.abilityStatus(you.seat, you.isTurn) };
  });

  /** The deck's top card Card Vision shows you, by name (DEC-059). */
  protected readonly nextCard = computed(() => {
    const next = this.board().next;
    return next ? this.cardName(next) : null;
  });

  /** The latest ability used, in words; a stolen card is named only when the server sent it, to its two players (DEC-059). */
  protected readonly lastAbility = computed(() => {
    const used = this.board().ability;
    if (!used) {
      return null;
    }
    const name = this.members().find((member) => member.seat === used.seat)?.ability?.name ?? used.ability;
    const action = `${this.nameOf(used.seat)} used ${name}`;
    if (used.target === undefined) {
      return `${action}.`;
    }
    const took = used.card ? this.cardName(used.card) : 'a card';
    return `${action} and took ${took} from ${this.nameOf(used.target, false)}.`;
  });

  /** What the latest move did, in words: who rolled what, where they landed and where the tile sent them. */
  protected readonly lastMove = computed(() => {
    const move = this.board().lastMove;
    const boost = move?.boost ? ` (+${move.boost} Speed Boost)` : '';
    return move ? this.landed(`${this.nameOf(move.seat)} rolled ${move.roll}${boost}`, move, move.seat) : null;
  });

  /** What the card played after the latest roll did, in words (DEC-057). */
  protected readonly lastPlay = computed(() => {
    const move = this.board().lastMove;
    return move?.play ? this.played(move.play, move.seat) : null;
  });

  constructor() {
    const tick = setInterval(() => this.now.set(Date.now()), 1000);
    const host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
    let resize: ResizeObserver | undefined;
    afterNextRender(() => {
      if (typeof ResizeObserver === 'function') {
        resize = new ResizeObserver(([entry]) => this.width.set(entry.contentRect.width));
        resize.observe(host);
      }
    });
    effect(() => {
      const move = this.board().lastMove;
      untracked(() => this.follow(move));
    });
    // Beside the players, the track takes the height left below its top; measured after each render, as the page moves.
    afterEveryRender(() => this.measure());
    const onResize = (): void => this.measure();
    globalThis.addEventListener?.('resize', onResize);
    inject(DestroyRef).onDestroy(() => {
      clearInterval(tick);
      clearTimeout(this.hopTimer);
      resize?.disconnect();
      globalThis.removeEventListener?.('resize', onResize);
    });
  }

  /**
   * Shows what's new in the latest move: the roll's hops, then the card played's, then the pawns it pushed back, together.
   * The first board's move and reduced motion show no hops; a pawn hopping stays where it was until its turn comes.
   */
  private follow(move: RaceMove | undefined): void {
    const roll = move ? JSON.stringify([move.seat, move.roll, move.from, move.landed, move.to]) : null;
    const play = move?.play ? JSON.stringify([move.seat, move.play]) : null;
    const first = this.seenRoll === undefined;
    const fresh = { roll: roll !== null && roll !== this.seenRoll, play: play !== null && play !== this.seenPlay };
    this.seenRoll = roll;
    this.seenPlay = play;
    if (!move || first || (!fresh.roll && !fresh.play) || reducedMotion()) {
      return;
    }
    // Each stage is the walks hopping together; a card's own move hops first, then the pawns it pushed back.
    const stages: Map<number, number[]>[] = [];
    if (fresh.roll) {
      stages.push(new Map([[move.seat, raceWalk(move.from, move.landed, move.to)]]));
    }
    const card = move.play;
    if (fresh.play && card) {
      if ((card.steps !== undefined || card.roll !== undefined) && card.from !== undefined && card.landed !== undefined && card.to !== undefined) {
        stages.push(new Map([[move.seat, raceWalk(card.from, card.landed, card.to)]]));
      }
      if (card.pushed?.length) {
        stages.push(new Map(card.pushed.map((push) => [push.seat, raceWalk(push.from, push.landed, push.to)])));
      }
    }
    if (stages.length === 0) {
      return;
    }
    const frames: Map<number, number>[] = [];
    const at = new Map<number, number>();
    for (const stage of stages) {
      stage.forEach((walk, seat) => at.has(seat) || at.set(seat, walk[0]));
    }
    for (const stage of stages) {
      const length = Math.max(...[...stage.values()].map((walk) => walk.length));
      for (let step = 0; step < length; step++) {
        stage.forEach((walk, seat) => at.set(seat, walk[Math.min(step, walk.length - 1)]));
        frames.push(new Map(at));
      }
    }
    clearTimeout(this.hopTimer);
    let frame = 0;
    const next = (): void => {
      this.hops.set(frame < frames.length ? frames[frame++] : null);
      if (this.hops()) {
        this.hopTimer = setTimeout(next, HOP_MS);
      }
    };
    next();
  }

  private measure(): void {
    const rect = this.track()?.nativeElement.getBoundingClientRect();
    let space: { width: number; height: number } | null = null;
    if (rect && rect.width > 0 && this.width() >= SIDE_BY_SIDE) {
      const top = rect.top + (globalThis.scrollY ?? 0);
      space = { width: Math.round(rect.width), height: Math.round(Math.max(LEAST_HEIGHT, (globalThis.innerHeight ?? 0) - top - 16)) };
    }
    const current = this.space();
    if (space?.width !== current?.width || space?.height !== current?.height) {
      this.space.set(space);
    }
  }

  /** Why your ability can't be used now, or what it's doing while armed (DEC-059). */
  private abilityStatus(seat: number, isTurn: boolean): string {
    const board = this.board();
    const ability = this.players().find((member) => member.seat === seat)?.ability;
    const pawn = this.armed().get(seat);
    if (pawn?.boost) {
      return `Armed: your next roll moves ${pawn.boost} more.`;
    }
    if (pawn?.shielded) {
      return 'Raised: it blocks the next Rug, Liquidated or Whale played on you.';
    }
    if (!ability?.usesLeft) {
      return 'Used.';
    }
    if (board.phase === 'Reply') {
      return 'Not while an attack waits for an answer.';
    }
    if (!isTurn) {
      return 'Use it on your turn.';
    }
    switch (ability.id) {
      case RACE_SPEED_BOOST:
        return 'Only before you roll.';
      case RACE_CARD_VISION:
        return 'The deck is empty.';
      case RACE_CARD_STEAL:
        return (board.hand?.length ?? 0) >= (board.maxHand ?? Infinity) ? 'Your hand is full.' : 'No opponent holds a card.';
      default:
        return 'Not now.';
    }
  }

  /** A seat's player: "You" (or "you" mid-sentence) for yours, otherwise their name. */
  private nameOf(seat: number, sentenceStart = true): string {
    const member = this.members().find((player) => player.seat === seat);
    return member?.isYou ? (sentenceStart ? 'You' : 'you') : (member?.displayName ?? `Seat ${seat}`);
  }

  /** A seat's player's, mid-sentence: "your" for yours, otherwise "Bob’s". */
  private possessive(seat: number): string {
    const name = this.nameOf(seat, false);
    return name === 'you' ? 'your' : `${name}’s`;
  }

  /** The sentence for a pawn's own move: `action` (who rolled or played what), where it landed and what the tile did. */
  private landed(action: string, landing: Landing, seat: number): string {
    const isYou = !!this.members().find((player) => player.seat === seat)?.isYou;
    const drew = landing.draw ? this.drew(landing.draw, landing.card, landing.reshuffled, isYou) : '';
    // A jump onto a Card tile draws there too: "…at tile 9, fell back to tile 7 and drew Rug."
    const then = drew ? ',' : ' and';
    switch (landing.effect) {
      case 'Boost':
        return `${action}, landed on the Boost at tile ${landing.landed}${then} jumped to tile ${landing.to}${drew}.`;
      case 'Setback':
        return `${action}, landed on the Setback at tile ${landing.landed}${then} fell back to tile ${landing.to}${drew}.`;
      case 'Card':
        return landing.draw
          ? `${action}, landed on the Card tile ${landing.to}${drew}.`
          : `${action} and landed on the Card tile ${landing.to}.`;
      case 'Finish':
        return this.board().winner === seat ? `${action}, reached the finish and won the race!` : `${action} and reached the finish.`;
      default:
        return `${action} and moved to tile ${landing.to}.`;
    }
  }

  /**
   * What a card play did: a move of the player's own, a swap, opponents pushed back or a skipped turn, and how the target
   * of an attack answered it (DEC-058).
   */
  private played(play: RacePlay, seat: number): string {
    const action = `${this.nameOf(seat)} played ${this.cardName(play.card)}`;
    if (play.target === undefined) {
      return this.effectOf(play, seat, action);
    }
    const target = this.nameOf(play.target, false);
    const attack = this.board().reply;
    if (play.reply === 'Blocked') {
      return `${action} on ${target}, blocked with HODL.`;
    }
    if (play.reply === 'Shielded') {
      return `${action} on ${target}, blocked by a Shield.`;
    }
    if (!play.reply && attack?.seat === seat && attack.target === play.target && this.board().phase === 'Reply') {
      return `${action} on ${target}, waiting for an answer.`;
    }
    const effect = this.effectOf(play, seat, action);
    return play.reply === 'TimedOut' ? `${effect} ${this.nameOf(play.target)} didn’t answer in time.` : effect;
  }

  /** What a card play did to the pawns, or to the turns. */
  private effectOf(play: RacePlay, seat: number, action: string): string {
    if (play.card === 'Liquidated' && play.target !== undefined) {
      const isYou = !!this.members().find((player) => player.seat === play.target)?.isYou;
      return `${action}: ${this.nameOf(play.target, false)} ${isYou ? 'skip your' : 'skips their'} next turn.`;
    }
    if (play.target !== undefined && play.to !== undefined) {
      const swapped = `${action} and swapped places with ${this.nameOf(play.target, false)} (now on tile ${play.from})`;
      return this.landed(swapped, { ...play, landed: play.to, to: play.to, card: play.drawn }, seat);
    }
    if (play.pushed) {
      return play.pushed.length > 0
        ? `${action}: ${play.pushed.map((push) => this.pushedTo(push)).join(', ')}.`
        : `${action}, but nobody could move back.`;
    }
    if (play.to === undefined || play.landed === undefined) {
      return `${action}.`;
    }
    const moved =
      play.roll !== undefined
        ? ` and rolled ${play.roll}`
        : play.steps !== undefined
          ? ` (${Math.abs(play.steps)} ${play.steps < 0 ? 'back' : 'forward'})`
          : '';
    return this.landed(`${action}${moved}`, { ...play, landed: play.landed, to: play.to, card: play.drawn }, seat);
  }

  /**
   * Where Reverse or Rug sent an opponent: back a few tiles, then on with the Boost or Setback landed on, and what a Card
   * tile there drew, named only to that player (revised in STORY-055).
   */
  private pushedTo(push: RacePush): string {
    const back = `${this.nameOf(push.seat, false)} back to tile ${push.landed}`;
    const isYou = !!this.members().find((player) => player.seat === push.seat)?.isYou;
    const drew = push.draw ? this.drew(push.draw, push.card, push.reshuffled, isYou) : '';
    switch (push.effect) {
      case 'Boost':
        return `${back}, then boosted to tile ${push.to}${drew}`;
      case 'Setback':
        return `${back}, then set back to tile ${push.to}${drew}`;
      default:
        return `${back}${drew}`;
    }
  }

  /** What a Card tile drew, as the end of the last-move sentence: the card by name only for the player who drew it. */
  private drew(draw: RaceDraw, drawn: RaceCard | undefined, reshuffledFirst: boolean | undefined, isYou: boolean): string {
    switch (draw) {
      case 'Drawn': {
        const card = drawn ? this.cardName(drawn) : 'a card';
        const reshuffled = reshuffledFirst ? ' after the discard pile was shuffled into a new deck' : '';
        return ` and drew ${card}${reshuffled}`;
      }
      case 'HandFull':
        return `, but ${isYou ? 'your' : 'their'} hand is full`;
      case 'NoCards':
        return ', but no card was left to draw';
      default:
        return '';
    }
  }

  protected cardName(card: RaceCard): string {
    return RACE_CARD_NAMES[card];
  }

  protected cardsOf(seat: number): string {
    const count = this.cardCounts().get(seat) ?? 0;
    return count === 1 ? '1 card' : `${count} cards`;
  }

  protected pawnsOn(tile: RaceTile): DrawnPawn[] {
    return this.pawnsByTile().get(tile.index) ?? [];
  }

  protected shownOn(tile: RaceTile): DrawnPawn[] {
    return this.shownByTile().get(tile.index) ?? [];
  }

  protected color(seat: number): string {
    return seatColor(seat);
  }

  /** What a screen reader hears for a tile: its number, what it is and who stands on it. */
  protected describe(tile: RaceTile): string {
    const what = [`Tile ${tile.index}`, this.label(tile) || 'Normal'];
    const pawns = this.pawnsOn(tile);
    if (pawns.length > 0) {
      what.push(pawns.map((pawn) => (pawn.isYou ? `${pawn.name} (you)` : pawn.name)).join(', '));
    }
    return what.join(', ');
  }

}
