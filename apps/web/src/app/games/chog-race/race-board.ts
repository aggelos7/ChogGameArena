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
import { diceList, timeLeft } from '../chog-life/life-board-model';
import { seatColor } from '../seats';
import {
  RACE_CARD_NAMES,
  RACE_CARD_STEAL,
  RACE_CARD_VISION,
  RACE_DOUBLE_DICE,
  RACE_FREEZE,
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
import { HolderBadge } from '../holder-badge';
import { ReportPhoto } from '../../moderation/report-photo';
import { DIE_MS, DieRoll, RollingDie } from '../rolling-die';
import { BANNER_MS, MoveBanner, MoveNews, RECENT_MOVES, RecentMoves, signedTilesText } from '../move-news';
import { SoundEffect, SoundEffects } from '../../shell/sound-effects';

/** A card you can play now, and for Whale, Rug and Liquidated the opponent it's played on (DEC-057, DEC-058). */
export interface RaceCardPlay {
  card: RaceCard;
  target?: number;
}

/** Your locked ability used now, and for Card Steal and Freeze the opponent it's used on (STORY-055, DEC-059; STORY-092). */
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

/** Which car sound a hop from `now` to `then` makes: reversing if any pawn goes back, the engine if one drives on. */
function carHeading(now: ReadonlyMap<number, number>, then: ReadonlyMap<number, number> | undefined): SoundEffect | null {
  if (!then) {
    return null;
  }
  const steps = [...then].map(([seat, tile]) => tile - (now.get(seat) ?? tile));
  return steps.some((step) => step < 0) ? 'carReverse' : steps.some((step) => step > 0) ? 'carForward' : null;
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
  imports: [ReportPhoto, RollingDie, MoveBanner, RecentMoves, HolderBadge],
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
  /** The move on the track has been shown: its die and hops are over, or it had none (DEC-076). */
  readonly settled = output<void>();
  readonly endTurn = output<void>();
  readonly playCard = output<RaceCardPlay>();
  /** Lets an attack on you land (DEC-058). */
  readonly pass = output<void>();
  /** Uses your locked ability (DEC-059). */
  readonly useAbility = output<RaceAbilityPlay>();

  /** Rolls keeping one of the dice Double Dice rolled for you (STORY-092, DEC-080). */
  readonly keepDie = output<number>();

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
  /** The die over the track while a new roll is shown, before its pawn hops (DEC-076). */
  protected readonly die = signal<DieRoll | null>(null);
  private rolls = 0;
  private readonly sounds = inject(SoundEffects);
  /** A move's die or hops are being shown (DEC-076). */
  readonly moving = signal(false);
  /**
   * The latest move as the page tells it: the server's, once its die and hops have been shown, so the words don't give
   * the roll away before the die lands (DEC-076).
   */
  private readonly revealed = signal<RaceMove | undefined>(undefined);
  /** What the move just shown did, over the track for a moment; null otherwise. */
  protected readonly banner = signal<MoveNews | null>(null);
  /** The popup over the tile the move just shown stopped on, while its banner shows. */
  protected readonly popup = computed(() => {
    const news = this.banner();
    return news?.popup ? { ...news.popup, tone: news.tone, id: news.id } : null;
  });
  /** The latest moves seen on this page, newest first. */
  protected readonly recent = signal<MoveNews[]>([]);
  /** The news of the move being shown, told once its hops are over. */
  private pending: MoveNews[] = [];
  private news = 0;
  private bannerTimer: ReturnType<typeof setTimeout> | undefined;
  /** The reversing sound playing while pawns go back (DEC-076), stopped when they stop; null when none is. */
  private carSound: SoundEffect | null = null;
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
        return phase === 'Move' && !pawn?.boost
          ? [{ abilityId: ability.id, label: `Use ${ability.name}: +${ability.values['tiles'] ?? 2} to your roll` }]
          : [];
      case RACE_SHIELD:
        return pawn?.shielded ? [] : [{ abilityId: ability.id, label: `Use ${ability.name}` }];
      case RACE_CARD_VISION:
        return (board.deck ?? 0) > 0 ? [{ abilityId: ability.id, label: `Use ${ability.name}: see the ${this.nextCards(ability.values['cards'])}` }] : [];
      case RACE_CARD_STEAL:
        if ((board.hand?.length ?? 0) >= (board.maxHand ?? Infinity)) {
          return [];
        }
        return board.pawns
          .filter((other) => other.seat !== you.seat && (other.cards ?? 0) > 0)
          .map((other) => ({ abilityId: ability.id, target: other.seat, label: `${ability.name}: from ${this.nameOf(other.seat, false)}` }));
      case RACE_DOUBLE_DICE:
        return phase === 'Move' && !pawn?.dice
          ? [{ abilityId: ability.id, label: `Use ${ability.name}: roll ${ability.values['dice'] ?? 2}, keep 1` }]
          : [];
      case RACE_FREEZE:
        if (pawn?.froze) {
          return [];
        }
        return board.pawns
          .filter((other) => other.seat !== you.seat && !this.skipping().has(other.seat))
          .map((other) => ({ abilityId: ability.id, target: other.seat, label: `${ability.name}: ${this.nameOf(other.seat, false)}` }));
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

  /** What Card Vision shows you: the deck's top cards by name, top first (DEC-059, DEC-077). */
  protected readonly nextCard = computed(() => {
    const board = this.board();
    const seen = board.seen ?? (board.next ? [board.next] : []);
    if (seen.length === 0) {
      return null;
    }
    const names = seen.map((card) => this.cardName(card)).join(', then ');
    return `${seen.length === 1 ? 'Next card' : `Next ${seen.length} cards`} in the deck: ${names}`;
  });

  /** The dice Double Dice rolled for you to keep one of, distinct and in order, while it's your turn; empty otherwise. */
  protected readonly yourDice = computed(() => {
    const you = this.turnOf();
    const pawn = you?.isYou && this.yourPhase() === 'Move' ? this.armed().get(you.seat) : undefined;
    return pawn?.dice && pawn.kept === undefined ? [...new Set(pawn.dice)].sort((a, b) => a - b) : [];
  });

  /**
   * Double Dice's dice waiting for a roll, in words, for everyone (STORY-092, DEC-080): "Your Double Dice rolled 1 and 3:
   * keep one to move by it." or "Alice’s Double Dice rolled 1 and 3." With a die kept when time ran out, which one the
   * next roll moves by.
   */
  protected readonly diceNote = computed(() => {
    const pawn = this.board().pawns.find((each) => each.dice);
    if (!pawn?.dice || !this.board().phase) {
      return null;
    }
    const isYou = !!this.members().find((member) => member.seat === pawn.seat)?.isYou;
    const rolled = `${isYou ? 'Your' : this.possessive(pawn.seat)} Double Dice rolled ${diceList(pawn.dice)}`;
    if (pawn.kept !== undefined) {
      return `${rolled}. Time ran out, so ${isYou ? 'your' : 'their'} next roll moves by ${pawn.kept}.`;
    }
    return isYou ? `${rolled}: keep one to move by it.` : `${rolled}.`;
  });

  /** A raised Shield's tag: "Shielded", with the attacks it still blocks when more than one (DEC-077). */
  protected shieldLabel(seat: number): string {
    const blocks = this.armed().get(seat)?.blocks ?? 1;
    return blocks > 1 ? `Shielded ×${blocks}` : 'Shielded';
  }

  /** How many cards seeing shows: "next card", or "next 2 cards" from a rarer Tier (DEC-077). */
  private nextCards(count = 1): string {
    return count === 1 ? 'next card' : `next ${count} cards`;
  }

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
    if (used.ability === RACE_FREEZE) {
      const target = this.nameOf(used.target, false);
      return `${action} on ${target}: ${target === 'you' ? 'you skip your' : 'they skip their'} next turn.`;
    }
    const cards = used.cards ?? (used.card ? [used.card] : []);
    const taken = used.taken ?? 1;
    const took = cards.length > 0
      ? cards.map((card) => this.cardName(card)).join(' and ')
      : taken === 1 ? 'a card' : `${taken} cards`;
    return `${action} and took ${took} from ${this.nameOf(used.target, false)}.`;
  });

  /** What the latest move did, in words: who rolled what, where they landed and where the tile sent them. */
  protected readonly lastMove = computed(() => {
    const move = this.revealed();
    const boost = move?.boost ? ` (+${move.boost} Speed Boost)` : '';
    return move ? this.landed(`${this.nameOf(move.seat)} ${this.rolled(move)}${boost}`, move, move.seat) : null;
  });

  /** What the card played after the latest roll did, in words (DEC-057). */
  protected readonly lastPlay = computed(() => {
    const move = this.revealed();
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
      clearTimeout(this.bannerTimer);
      this.stopCar();
      resize?.disconnect();
      globalThis.removeEventListener?.('resize', onResize);
    });
  }

  /**
   * Shows what's new in the latest move: a new roll's die tumbling and landing on its number (DEC-076), then the roll's
   * hops, then the card played's, then the pawns it pushed back, together. An engine starts as a pawn drives on, played to
   * its end, and a back-up beeper sounds while one goes back (a card's move backwards, or pushed back), until it stops. The first board's move and
   * reduced motion show none of it; a pawn hopping stays where it was until its turn comes.
   */
  private follow(move: RaceMove | undefined): void {
    const roll = move ? JSON.stringify([move.seat, move.roll, move.from, move.landed, move.to]) : null;
    const play = move?.play ? JSON.stringify([move.seat, move.play]) : null;
    const first = this.seenRoll === undefined;
    const fresh = { roll: roll !== null && roll !== this.seenRoll, play: play !== null && play !== this.seenPlay };
    this.seenRoll = roll;
    this.seenPlay = play;
    if (move) {
      this.pending.push(...this.newsOf(move, fresh));
    }
    if (!move || first || (!fresh.roll && !fresh.play) || reducedMotion()) {
      this.settle(first || reducedMotion(), !first);
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
      this.settle(false, true);
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
    this.stopCar();
    this.moving.set(true);
    let frame = 0;
    let heading: SoundEffect | null = null;
    const next = (): void => {
      this.die.set(null);
      const shown = frames[frame];
      // The sound follows the hop about to happen: the engine starts as a car drives on, and the beeper sounds while one
      // goes back (a Setback tile, a card's move backwards, pushed back), until it stops going back.
      const going = shown ? carHeading(shown, frames[frame + 1]) : null;
      frame++;
      if (going !== heading) {
        this.stopCar();
        if (going) {
          this.sounds.play(going);
          // The engine start is short and always heard whole; only the beeper stops with the car.
          this.carSound = going === 'carReverse' ? going : null;
        }
        heading = going;
      }
      this.hops.set(shown ?? null);
      if (shown) {
        this.hopTimer = setTimeout(next, HOP_MS);
      } else {
        this.moving.set(false);
        this.reveal(true);
        this.settled.emit();
      }
    };
    if (fresh.roll) {
      this.die.set({ value: move.roll, id: ++this.rolls });
      this.hops.set(frames[0]);
      this.hopTimer = setTimeout(next, DIE_MS);
    } else {
      next();
    }
  }

  /**
   * Says the track shows its move in full, telling it (with a banner when `announce`), unless one is still being shown;
   * `cut` ends one being shown (the first board, reduced motion).
   */
  private settle(cut: boolean, announce: boolean): void {
    if (cut) {
      clearTimeout(this.hopTimer);
      this.hops.set(null);
      this.die.set(null);
      this.stopCar();
      this.moving.set(false);
    }
    if (!this.moving()) {
      this.reveal(announce);
      this.settled.emit();
    }
  }

  /** Tells the move shown: the last-move lines, Recent moves and, when `announce`, the banner and popup of its news. */
  private reveal(announce: boolean): void {
    this.revealed.set(this.board().lastMove);
    const told = this.pending;
    this.pending = [];
    if (told.length === 0) {
      return;
    }
    this.recent.update((recent) => [...told.reverse(), ...recent].slice(0, RECENT_MOVES));
    if (announce) {
      clearTimeout(this.bannerTimer);
      this.banner.set(told[0]);
      this.bannerTimer = setTimeout(() => this.banner.set(null), BANNER_MS);
    }
  }

  /** The news of what's new in a move: its roll, then the card played after it. */
  private newsOf(move: RaceMove, fresh: { roll: boolean; play: boolean }): MoveNews[] {
    const news: MoveNews[] = [];
    if (fresh.roll) {
      const boost = move.boost ? ` (+${move.boost})` : '';
      news.push({ id: ++this.news, title: `${this.nameOf(move.seat)} ${this.rolled(move)}${boost}`, ...this.landing(move, move.seat) });
    }
    const play = move.play;
    if (fresh.play && play) {
      const action = `${this.nameOf(move.seat)} played ${this.cardName(play.card)}`;
      const moved = play.from !== undefined && play.to !== undefined ? play.to - play.from : 0;
      const told =
        play.from !== undefined && play.landed !== undefined && play.to !== undefined && play.effect
          ? this.landing({ landed: play.landed, to: play.to, effect: play.effect }, move.seat)
          : null;
      const sentence = this.played(play, move.seat);
      const rest = sentence.startsWith(action) ? sentence.slice(action.length).replace(/^[ ,]+/, '') : sentence;
      news.push({
        id: ++this.news,
        title: action,
        detail: told?.detail ?? (moved !== 0 ? `${signedTilesText(moved)}, to tile ${play.to}` : rest.charAt(0).toUpperCase() + rest.slice(1)),
        tone: told?.tone ?? (moved > 0 ? 'good' : moved < 0 ? 'bad' : 'neutral'),
        popup: moved !== 0 && play.to !== undefined ? { tile: play.to, text: signedTilesText(moved) } : null,
      });
    }
    return news;
  }

  /** What a landing did, for news: a Boost or Setback's jump, a Card tile, the finish, or the tile reached. */
  private landing(landing: Pick<Landing, 'landed' | 'to' | 'effect'>, seat: number): Omit<MoveNews, 'id' | 'title'> {
    const jump = landing.to - landing.landed;
    switch (landing.effect) {
      case 'Boost':
        return { detail: `Boost! Jumped to tile ${landing.to}`, tone: 'good', popup: { tile: landing.to, text: signedTilesText(jump) } };
      case 'Setback':
        return { detail: `Setback! Back to tile ${landing.to}`, tone: 'bad', popup: { tile: landing.to, text: signedTilesText(jump) } };
      case 'Card':
        return { detail: `Card tile: drew a card`, tone: 'neutral', popup: { tile: landing.to, text: 'Card!' } };
      case 'Finish':
        return this.board().winner === seat
          ? { detail: 'Reached the finish and won the race!', tone: 'good', popup: { tile: landing.to, text: 'Winner!' } }
          : { detail: 'Reached the finish!', tone: 'good', popup: { tile: landing.to, text: 'Finish!' } };
      default:
        return { detail: `Moved to tile ${landing.to}`, tone: 'neutral', popup: null };
    }
  }

  private stopCar(): void {
    if (this.carSound) {
      this.sounds.stop(this.carSound);
      this.carSound = null;
    }
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
    if (pawn?.dice) {
      return pawn.kept === undefined ? 'Rolled: move by the die you keep.' : `Your next roll moves by ${pawn.kept}.`;
    }
    if (pawn?.shielded) {
      const blocks = pawn.blocks ?? 1;
      return blocks === 1
        ? 'Raised: it blocks the next Rug, Liquidated or Whale played on you.'
        : `Raised: it blocks the next ${blocks} Rugs, Liquidateds or Whales played on you.`;
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
      case RACE_DOUBLE_DICE:
        return 'Only before you roll.';
      case RACE_FREEZE:
        return pawn?.froze ? 'Used this turn: one Freeze a turn.' : 'Every opponent already skips a turn.';
      case RACE_CARD_VISION:
        return 'The deck is empty.';
      case RACE_CARD_STEAL:
        return (board.hand?.length ?? 0) >= (board.maxHand ?? Infinity) ? 'Your hand is full.' : 'No opponent holds a card.';
      default:
        return 'Not now.';
    }
  }

  /** A roll in words: "rolled 3", or after Double Dice "rolled 1 and 3 with Double Dice and kept 3" (STORY-092, DEC-080). */
  private rolled(move: RaceMove): string {
    return move.dice ? `rolled ${diceList(move.dice)} with Double Dice and kept ${move.roll}` : `rolled ${move.roll}`;
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
