import {
  Component,
  DestroyRef,
  ElementRef,
  Injector,
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
import { RoomAbility, RoomMember } from '../../rooms/rooms-api';
import { reducedMotion } from '../chog-life/life-board';
import { HolderBadge } from '../holder-badge';
import { RoyaleSeat, RoyaleSeatView, SeatReveal, TableCard, TableSeat } from './royale-seat';
import {
  CARD_ACCENT,
  CARD_INFO,
  EVENT_INFO,
  RevealStep,
  RoyaleBoard,
  RoyaleCard,
  RoyaleHandCard,
  RoyaleRound,
  describeEntry,
  revealDuration,
  revealSteps,
  signedMon,
  stepAt,
} from './royale-board-model';

/** A round's market event is announced this long before its decisions open (rules version 1 and 2, ROYALE-004). */
export const ANNOUNCEMENT_MS = 5_000;

/** A round resolves this long before the next one's announcement: its reveal and the transition (rules version 1 and 2). */
export const REVEAL_AND_TRANSITION_MS = 15_000;

/**
 * A round is revealed on the table only when it reaches you this soon after it resolved: one that comes later, after a
 * reconnect, shows its final state at once instead (STORY-095).
 */
export const REVEAL_FRESH_MS = 3_000;

/** The table holds a reveal's final state this long before the reveal dialog opens, so the played cards can be read. */
export const REVEAL_HOLD_MS = 2_500;

/** The last seconds of a decision window, shown as urgent. */
export const URGENT_SECONDS = 5;

/** What the round is doing now, on the server's clock (DEC-081, DEC-082). */
export type RoyalePhase = 'reveal' | 'announcement' | 'deciding' | 'resolving';

/**
 * An action to send: a card with its target, or HODL, and whether it is locked in (DEC-083). The board always locks it:
 * choosing is final, so the round ends once everyone has chosen (DEC-092).
 */
export interface RoyaleSubmission {
  card?: string;
  target?: number;
  hodl?: true;
  lock: boolean;
}

/** Your ability to use, on an opponent's seat for Insider Info (STORY-093, DEC-095). */
export interface RoyaleAbilityUse {
  abilityId: string;
  target?: number;
}

function tableCard(card: RoyaleCard | null): TableCard {
  return card
    ? { id: card, name: CARD_INFO[card].name, icon: CARD_INFO[card].icon, accent: CARD_ACCENT[card] }
    : { id: 'Hodl', name: 'HODL', icon: '✋', accent: CARD_ACCENT.Hodl };
}

/**
 * The Chog Royale board (STORY-081, ROYALE-045–048, 051): the round and its countdown on the server's clock (STORY-083),
 * the market event, every player with their MON, cards held and whether they have submitted or locked (never what), your
 * three cards, a target picker for targeted cards listing only players you can aim at, HODL, Choose, which is final
 * for the round, and your ability, with its uses left and a "Holder" badge for a holder-only one (DEC-078), aimed at an
 * opponent for Insider Info, whose hand then shows only to you for the round (STORY-093, DEC-095). Your pending action comes from the server's view, so after a reload or reconnect you see
 * what you chose; nothing here changes a balance before the server says so. Between rounds the reveal (STORY-082,
 * ROYALE-049) lists what each player did, in resolution order, as text, playing in briefly unless motion is reduced.
 * Each round opens with its announcement in a dialog over the board: the round, its market event and the countdown to
 * decisions. It closes when decisions open, or earlier with its button or Escape. Each round's reveal shows in a dialog
 * too, your own line in bold, until the next announcement. The last round is revealed like the others: when the match
 * has just ended, the room page keeps the board up with that reveal and a countdown to the results, which "See results"
 * skips.
 *
 * Since STORY-094 the players sit around a table (DEC-097): opponents in an arc across the top, you at the bottom, the
 * round and market on the felt; seats show avatars, MON, face-down cards and the decision status, never a card's face but
 * the ones your Insider Info shows you. Your hand is drawn as cards below the table, with your choice and ability in a panel
 * beside them on wide screens, and a targeted card is aimed by tapping a seat, each of which is a labelled radio. A round watched live is revealed on the table first (STORY-095): in resolution order,
 * cards flip at their seats, targeted ones fly to their targets, what they did shows where they land and balances count
 * to the server's; the reveal dialog follows 2.5 s later. A reload, a late snapshot, one of a later round or reduced motion shows
 * the final state at once.
 */
@Component({
  selector: 'app-royale-board',
  templateUrl: './royale-board.html',
  styleUrl: './royale-board.scss',
  imports: [HolderBadge, RoyaleSeatView],
})
export class RoyaleBoardView {
  readonly board = input.required<RoyaleBoard>();
  readonly members = input.required<RoomMember[]>();
  /** When the current round opens and closes (ISO 8601), from the room. */
  readonly opensAt = input<string | null>(null);
  readonly endsAt = input<string | null>(null);
  /** How far the server's clock is ahead of this device's, in milliseconds (STORY-083). */
  readonly clockOffset = input(0);
  /** A command is on its way; the controls wait for its answer. */
  readonly busy = input(false);
  /**
   * The match has just ended: until this time (this device's clock, in milliseconds) the board shows the last round's
   * reveal before the results. Null while the match is played.
   */
  readonly finalRevealUntil = input<number | null>(null);

  readonly act = output<RoyaleSubmission>();
  readonly useAbility = output<RoyaleAbilityUse>();
  /** Skip the last round's reveal and show the results now. */
  readonly seeResults = output<void>();

  protected readonly cardInfo = CARD_INFO;
  protected readonly cardAccent = CARD_ACCENT;
  protected readonly eventInfo = EVENT_INFO;

  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  /** The server's time now, ticking four times a second, and every 50 ms while a reveal plays. */
  private readonly now = signal(Date.now());
  private frames: ReturnType<typeof setInterval> | undefined;

  constructor() {
    const tick = setInterval(() => this.now.set(Date.now()), 250);
    inject(DestroyRef).onDestroy(() => {
      clearInterval(tick);
      clearInterval(this.frames);
    });
    // A snapshot of another round ends a reveal in play; one that brings the round you were just playing, freshly
    // resolved, starts its reveal on the table.
    effect(() => {
      const board = this.board();
      untracked(() => this.follow(board));
    });
    effect(() => {
      if (!this.animating() && this.frames !== undefined) {
        clearInterval(this.frames);
        this.frames = undefined;
      }
    });
    // The announcement and reveal dialogs are modal while they show: the board behind them takes no actions then anyway.
    effect(() => showModal(this.announcementDialog()?.nativeElement, this.announcing()));
    effect(() => showModal(this.revealDialog()?.nativeElement, this.revealing()));
    // A new round, or a change in the server's view of your action, resets what you have picked to what the server
    // holds; another player's snapshot doesn't, so a pick waiting for its target survives it.
    effect(() => {
      this.serverChoice();
      const action = untracked(() => this.board().action);
      this.picked.set(action?.card ?? (action?.hodl ? 'hodl' : null));
      this.target.set(action?.target ?? null);
    });
  }

  private readonly announcementDialog = viewChild<ElementRef<HTMLDialogElement>>('announcement');

  /** The round whose announcement you closed early. */
  private readonly dismissedRound = signal<number | null>(null);

  /** The round's announcement is showing: its event is out and decisions haven't opened, unless you closed it. */
  protected readonly announcing = computed(() => this.phase() === 'announcement' && this.dismissedRound() !== this.board().round);

  protected dismissAnnouncement(): void {
    this.dismissedRound.set(this.board().round);
  }

  private readonly revealDialog = viewChild<ElementRef<HTMLDialogElement>>('reveal');

  /** The round whose reveal you closed early. */
  private readonly dismissedReveal = signal<number | null>(null);

  /** A round's reveal is showing: between rounds, or the last one before the results, unless you closed it. */
  protected readonly revealing = computed(() => {
    const round = this.lastRound();
    return this.phase() === 'reveal' && !!round && this.dismissedReveal() !== round.round && !this.holding();
  });

  /**
   * The reveal dialog closed by its button or Escape: between rounds the board shows again; after the last round the
   * results come now.
   */
  protected closeReveal(): void {
    if (!this.revealing()) {
      return;
    }
    if (this.finished()) {
      this.seeResults.emit();
    } else {
      this.dismissedReveal.set(this.lastRound()?.round ?? null);
    }
  }

  /** Your seat, so your own lines stand out. */
  protected readonly yourSeat = computed(() => this.you()?.seat ?? null);

  /** The round and your action as the server holds them, as one value that changes only when they do. */
  private readonly serverChoice = computed(() => {
    const { round, action } = this.board();
    return `${round}|${action?.hodl ?? ''}|${action?.card ?? ''}|${action?.target ?? ''}`;
  });

  private readonly serverNow = computed(() => this.now() + this.clockOffset());

  private readonly opens = computed(() => parse(this.opensAt()));
  private readonly ends = computed(() => parse(this.endsAt()));

  /** The match has ended and the board shows its last round's reveal. */
  protected readonly finished = computed(() => this.finalRevealUntil() !== null);

  protected readonly phase = computed<RoyalePhase>(() => {
    if (this.finished()) {
      return 'reveal';
    }
    const now = this.serverNow();
    if (now >= this.ends()) {
      return 'resolving';
    }
    if (now >= this.opens()) {
      return 'deciding';
    }
    return this.lastRound() && now < this.opens() - ANNOUNCEMENT_MS ? 'reveal' : 'announcement';
  });

  /** Whole seconds left in the phase, rounded up. */
  protected readonly secondsLeft = computed(() => {
    const final = this.finalRevealUntil();
    if (final !== null) {
      return Math.max(0, Math.ceil((final - this.now()) / 1000));
    }
    const now = this.serverNow();
    const until = { reveal: this.opens() - ANNOUNCEMENT_MS, announcement: this.opens(), deciding: this.ends(), resolving: now }[this.phase()];
    return Number.isFinite(until) ? Math.max(0, Math.ceil((until - now) / 1000)) : 0;
  });

  protected readonly urgent = computed(() => this.phase() === 'deciding' && this.secondsLeft() <= URGENT_SECONDS);

  protected readonly timerText = computed(() => {
    const left = this.secondsLeft();
    switch (this.phase()) {
      case 'reveal':
        return this.finished() ? `Results in ${left}s` : `Next round in ${left + ANNOUNCEMENT_MS / 1000}s`;
      case 'announcement':
        return `Decisions open in ${left}s`;
      case 'deciding':
        return `${left}s left to decide`;
      default:
        return 'Revealing…';
    }
  });

  /** The round just resolved, shown in the reveal; null before the first. */
  protected readonly lastRound = computed<RoyaleRound | null>(() => {
    const ledger = this.board().ledger;
    const last = ledger.length > 0 ? ledger[ledger.length - 1] : null;
    return last && (this.finished() || last.round === this.board().round - 1) ? last : null;
  });

  /** The reveal's lines, in the order the round resolved (DEC-085). */
  protected readonly revealLines = computed(() => {
    const round = this.lastRound();
    return round ? orderedLines(round, this.nameOf) : [];
  });

  protected readonly you = computed(() => this.members().find((member) => member.isYou) ?? null);

  protected readonly seats = computed<RoyaleSeat[]>(() => {
    const players = new Map(this.board().players.map((player) => [player.seat, player]));
    return this.members()
      .flatMap((member) => (member.seat === null ? [] : [{ member, seat: member.seat }]))
      .sort((a, b) => a.seat - b.seat)
      .map(({ member, seat }) => {
        const player = players.get(seat);
        return {
          seat,
          name: member.displayName,
          isYou: member.isYou,
          avatar: member.avatar,
          mon: player?.mon ?? 0,
          cards: player?.cards ?? 0,
          status: member.hasLeft || !player
            ? 'left'
            : this.finished()
              ? 'final'
              : member.hasLocked
                ? 'locked'
                : member.hasSubmitted
                  ? 'submitted'
                  : 'deciding',
        };
      });
  });

  /** You may act: the round is open, you still play and haven't locked your action. */
  protected readonly canAct = computed(() => {
    const you = this.you();
    return this.phase() === 'deciding' && !!you && !you.hasLeft && !you.hasLocked && !this.busy();
  });

  protected readonly locked = computed(() => !!this.you()?.hasLocked);

  /** Your cards and your round's panel are on show: you still play and the match goes on. */
  protected readonly deciding = computed(() => {
    const you = this.you();
    return !!you && !you.hasLeft && !this.finished();
  });

  /** The card id you have picked, `hodl`, or nothing yet. */
  protected readonly picked = signal<string | null>(null);
  protected readonly target = signal<number | null>(null);

  protected readonly pickedCard = computed<RoyaleHandCard | null>(() => this.board().hand.find((card) => card.id === this.picked()) ?? null);

  protected readonly needsTarget = computed(() => {
    const card = this.pickedCard();
    return !!card && CARD_INFO[card.card].targeted;
  });

  /** Everyone else seated: only players still playing can be aimed at; the others are listed disabled (ROYALE-047). */
  protected readonly targets = computed(() => this.seats().filter((seat) => !seat.isYou));

  /** What you have picked is a complete action: HODL, an untargeted card, or a targeted card with its target. */
  protected readonly ready = computed(() => this.picked() === 'hodl' || (!!this.pickedCard() && (!this.needsTarget() || this.target() !== null)));

  /** Your action as the server holds it, in words. */
  protected readonly pendingText = computed(() => {
    const action = this.board().action;
    if (!action) {
      return this.phase() === 'deciding' ? 'You haven’t chosen yet: if time runs out, you HODL.' : null;
    }
    const what = action.hodl ? 'HODL' : this.describeCard(action.card, action.target);
    return `You chose ${what}.`;
  });

  /** Your locked ability while it has a use left. */
  protected readonly ability = computed<RoomAbility | null>(() => {
    const ability = this.you()?.ability;
    return ability && ability.usesLeft !== 0 ? ability : null;
  });

  /**
   * Your ability's buttons: one, or for an ability aimed at an opponent, such as Insider Info, one per opponent still
   * playing (STORY-093, DEC-095).
   */
  protected readonly abilityPlays = computed<{ use: RoyaleAbilityUse; label: string }[]>(() => {
    const ability = this.ability();
    if (!ability) {
      return [];
    }
    if (ability.target !== 'Opponent') {
      return [{ use: { abilityId: ability.id }, label: `Use ${ability.name}` }];
    }
    return this.targets()
      .filter((seat) => seat.status !== 'left')
      .map((seat) => ({ use: { abilityId: ability.id, target: seat.seat }, label: `${ability.name} on ${seat.name}` }));
  });

  protected readonly usedAbility = computed(() => {
    const used = this.board().ability;
    const mine = this.you()?.ability;
    const insider = this.board().insider;
    const name = used ? (mine?.id === used ? mine.name : used) : null;
    return name && insider ? `${name} on ${this.nameOf(insider.seat)}` : name;
  });

  /** The hand your Insider Info shows you this round, only you (STORY-093, DEC-095). */
  protected readonly insider = computed(() => {
    const insider = this.board().insider;
    return insider ? { name: this.nameOf(insider.seat), cards: insider.hand.map((card) => CARD_INFO[card.card]) } : null;
  });

  /** The opponents around the top of the table, starting from the seat after yours, so turn order runs left to right. */
  protected readonly opponents = computed<TableSeat[]>(() => {
    const yours = this.yourSeat() ?? 0;
    const others = this.seats().filter((seat) => !seat.isYou);
    const ordered = [...others.filter((seat) => seat.seat > yours), ...others.filter((seat) => seat.seat < yours)];
    const middle = (ordered.length - 1) / 2;
    return ordered.map((seat, index) => this.toTableSeat(seat, middle > 0 ? Math.abs(index - middle) / middle : 0));
  });

  /** Your seat, at the bottom of the table. */
  protected readonly mySeat = computed<TableSeat | null>(() => {
    const mine = this.seats().find((seat) => seat.isYou);
    return mine ? this.toTableSeat(mine, 0) : null;
  });

  /** A targeted card is picked: the opponents' seats take the aim. */
  protected readonly aiming = computed(() => this.needsTarget());

  /** The picked card's name while it needs a target, for the seats' aim. */
  protected readonly aimWith = computed(() => {
    const card = this.pickedCard();
    return card && this.needsTarget() ? CARD_INFO[card.card].name : null;
  });

  private toTableSeat(seat: RoyaleSeat, arc: number): TableSeat {
    const insider = this.board().insider;
    return {
      ...seat,
      arc,
      peek: insider?.seat === seat.seat && !this.revealRound() ? insider.hand.map((card) => tableCard(card.card)) : null,
      ...this.revealAt(seat),
    };
  }

  /** What a seat shows at this moment of the round's reveal, and its balance then; the server's after it. */
  private revealAt(seat: RoyaleSeat): { reveal: SeatReveal | null; mon: number } {
    const steps = this.shownSteps();
    if (!this.revealRound()) {
      return { reveal: null, mon: seat.mon };
    }
    const elapsed = this.elapsed();
    const step = steps.find((s) => s.seat === seat.seat) ?? null;
    const at = step ? stepAt(step, elapsed) : null;
    const ability = step && at?.shown && step.ability ? step.ability + (step.abilityTarget !== null ? ` on ${this.nameOf(step.abilityTarget)}` : '') : null;
    return {
      mon: step && at ? at.mon : seat.mon,
      reveal: {
        card: step && at?.shown ? tableCard(step.card) : null,
        ability,
        impacts: steps.flatMap((s) => (s.target === seat.seat && s.impact && stepAt(s, elapsed).landed ? [s.impact] : [])),
        change: step && at?.landed ? signedMon(step.change).replace(' MON', '') : null,
        gain: !!step && step.change > 0,
        loss: !!step && step.change < 0,
      },
    };
  }

  /** The previous snapshot's round and last resolved round, to tell a round you watched resolve from one you came back to. */
  private watched: { round: number; resolved: number } | null = null;

  /** The reveal playing on the table: its round, its steps and when it started on this device's clock. */
  private readonly animation = signal<{ round: number; steps: RevealStep[]; duration: number; startedAt: number } | null>(null);

  /** Every seat's centre on the table, measured when a reveal starts, for the cards that fly between them. */
  private readonly seatCenters = signal(new Map<number, { x: number; y: number }>());

  /** The round revealed on the table: the last one resolved, between rounds or after the last. */
  protected readonly revealRound = computed(() => (this.phase() === 'reveal' ? this.lastRound() : null));

  /** The reveal is still playing on the table. */
  protected readonly animating = computed(() => this.onTable(0));

  /** The reveal is playing on the table, or holding its final state there a moment; the reveal dialog waits for it. */
  private readonly holding = computed(() => this.onTable(REVEAL_HOLD_MS));

  private onTable(hold: number): boolean {
    const animation = this.animation();
    return !!animation && animation.round === this.revealRound()?.round && this.now() - animation.startedAt < animation.duration + hold;
  }

  /** How far into the reveal the table is; past its end when nothing plays, so everything shows at once. */
  private readonly elapsed = computed(() => {
    const animation = this.animation();
    return this.animating() && animation ? this.now() - animation.startedAt : Number.POSITIVE_INFINITY;
  });

  private readonly shownSteps = computed<RevealStep[]>(() => {
    const round = this.revealRound();
    const animation = this.animation();
    return !round ? [] : animation?.round === round.round ? animation.steps : revealSteps(round);
  });

  /** The cards in the air right now, from their player's seat towards their target's. */
  protected readonly flying = computed(() => {
    const elapsed = this.elapsed();
    const centers = this.seatCenters();
    return this.shownSteps().flatMap((step) => {
      const from = centers.get(step.seat);
      const to = step.target !== null ? centers.get(step.target) : undefined;
      return step.card && from && to && stepAt(step, elapsed).flying
        ? [{ seat: step.seat, card: tableCard(step.card), x: from.x, y: from.y, dx: to.x - from.x, dy: to.y - from.y }]
        : [];
    });
  });

  /**
   * Takes in a new snapshot: one of another round ends a reveal in play rather than queuing behind it, and one that
   * brings, freshly resolved, the round the previous snapshot was playing starts the table's reveal. A first snapshot,
   * after a reload, has nothing to replay.
   */
  private follow(board: RoyaleBoard): void {
    const last = board.ledger.length > 0 ? board.ledger[board.ledger.length - 1] : null;
    const playing = this.animation();
    if (playing && last?.round === playing.round && board.round === this.watched?.round) {
      // Another snapshot of the same moment, such as someone leaving: the reveal plays on.
      return;
    }
    this.animation.set(null);
    const watched = this.watched;
    this.watched = { round: board.round, resolved: last?.round ?? 0 };
    if (!last || !watched || last.round <= watched.resolved || watched.round !== last.round || reducedMotion() || !this.fresh()) {
      return;
    }
    const steps = revealSteps(last);
    const duration = revealDuration(steps);
    if (duration === 0) {
      return;
    }
    this.now.set(Date.now());
    this.animation.set({ round: last.round, steps, duration, startedAt: Date.now() });
    clearInterval(this.frames);
    this.frames = setInterval(() => this.now.set(Date.now()), 50);
    afterNextRender(() => this.measureSeats(), { injector: this.injector });
  }

  /** The round just resolved reached you in time to replay it: the match's end, or within moments of the round's end. */
  private fresh(): boolean {
    if (this.finished()) {
      return true;
    }
    const resolvedAt = this.opens() - ANNOUNCEMENT_MS - REVEAL_AND_TRANSITION_MS;
    return this.serverNow() - resolvedAt <= REVEAL_FRESH_MS;
  }

  private measureSeats(): void {
    const table = this.host.nativeElement.querySelector<HTMLElement>('[data-table]');
    if (!table) {
      return;
    }
    const origin = table.getBoundingClientRect();
    const centers = new Map<number, { x: number; y: number }>();
    table.querySelectorAll<HTMLElement>('[data-seat]').forEach((element) => {
      const box = element.getBoundingClientRect();
      centers.set(Number(element.dataset['seat']), { x: box.left + box.width / 2 - origin.left, y: box.top + box.height / 2 - origin.top });
    });
    this.seatCenters.set(centers);
  }

  /** A hand card's tilt and drop in the fan, from the middle outwards. */
  protected fan(index: number, count: number): { tilt: number; drop: number } {
    const offset = index - (count - 1) / 2;
    return { tilt: offset * 4, drop: Math.abs(offset) * 0.375 };
  }

  protected pick(card: string): void {
    if (this.picked() !== card) {
      this.picked.set(card);
      this.target.set(null);
    }
  }

  protected send(): void {
    const lock = true;
    const card = this.pickedCard();
    const target = this.target();
    if (!this.ready() || !this.canAct()) {
      return;
    }
    if (this.picked() === 'hodl') {
      this.act.emit({ hodl: true, lock });
    } else if (card && !this.needsTarget()) {
      this.act.emit({ card: card.id, lock });
    } else if (card && target !== null) {
      this.act.emit({ card: card.id, target, lock });
    }
  }

  protected readonly nameOf = (seat: number): string => {
    const member = this.members().find((m) => m.seat === seat);
    return member?.isYou ? 'You' : (member?.displayName ?? `Seat ${seat}`);
  };

  private describeCard(id: string | null, target: number | null): string {
    const card = this.board().hand.find((c) => c.id === id);
    if (!card) {
      return 'a card';
    }
    const name = CARD_INFO[card.card].name;
    return target !== null ? `${name} at ${this.nameOf(target)}` : name;
  }

  /** A round's lines for the history, in resolution order. */
  protected linesOf(round: RoyaleRound): { seat: number; text: string; change: number }[] {
    return orderedLines(round, this.nameOf);
  }

  protected cardName(card: RoyaleCard): string {
    return CARD_INFO[card].name;
  }
}

/** Opens `dialog` as a modal, or closes it, to match `show`; a test DOM without `showModal` gets the `open` attribute. */
function showModal(dialog: HTMLDialogElement | undefined, show: boolean): void {
  if (!dialog || show === dialog.open) {
    return;
  }
  if (show) {
    if (typeof dialog.showModal === 'function') {
      dialog.showModal();
    } else {
      dialog.setAttribute('open', '');
    }
  } else if (typeof dialog.close === 'function') {
    dialog.close();
  } else {
    dialog.removeAttribute('open');
  }
}

function parse(at: string | null): number {
  const time = at ? Date.parse(at) : Number.NaN;
  return Number.isNaN(time) ? Number.POSITIVE_INFINITY : time;
}

/** A round's lines in resolution order: seat priority, as the server resolved them (DEC-085). */
export function orderedLines(round: RoyaleRound, name: (seat: number) => string): { seat: number; text: string; change: number }[] {
  const order = (seat: number) => {
    const index = round.priority.indexOf(seat);
    return index < 0 ? Number.MAX_SAFE_INTEGER : index;
  };
  return [...round.entries]
    .sort((a, b) => order(a.seat) - order(b.seat))
    .map((entry) => ({ seat: entry.seat, text: describeEntry(entry, name), change: entry.change }));
}
