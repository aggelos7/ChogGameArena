import { Component, DestroyRef, ElementRef, computed, effect, inject, input, output, signal, untracked, viewChild } from '@angular/core';
import { RoomAbility, RoomMember } from '../../rooms/rooms-api';
import {
  CARD_INFO,
  EVENT_INFO,
  RoyaleBoard,
  RoyaleCard,
  RoyaleHandCard,
  RoyaleRound,
  describeEntry,
} from './royale-board-model';

/** A round's market event is announced this long before its decisions open (rules version 1 and 2, ROYALE-004). */
export const ANNOUNCEMENT_MS = 5_000;

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

/** One player as the board lists them. */
interface RoyaleSeat {
  seat: number;
  name: string;
  isYou: boolean;
  mon: number;
  cards: number;
  status: 'locked' | 'submitted' | 'deciding' | 'left' | 'final';
}

/**
 * The Chog Royale board (STORY-081, ROYALE-045–048, 051): the round and its countdown on the server's clock (STORY-083),
 * the market event, every player with their MON, cards held and whether they have submitted or locked (never what), your
 * three cards, a target picker for targeted cards listing only players you can aim at, HODL, Choose, which is final
 * for the round, and your ability. Your pending action comes from the server's view, so after a reload or reconnect you see
 * what you chose; nothing here changes a balance before the server says so. Between rounds the reveal (STORY-082,
 * ROYALE-049) lists what each player did, in resolution order, as text, playing in briefly unless motion is reduced.
 * Each round opens with its announcement in a dialog over the board: the round, its market event and the countdown to
 * decisions. It closes when decisions open, or earlier with its button or Escape. Each round's reveal shows in a dialog
 * too, your own line in bold, until the next announcement. The last round is revealed like the others: when the match
 * has just ended, the room page keeps the board up with that reveal and a countdown to the results, which "See results"
 * skips.
 */
@Component({
  selector: 'app-royale-board',
  templateUrl: './royale-board.html',
  styleUrl: './royale-board.scss',
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
  readonly useAbility = output<string>();
  /** Skip the last round's reveal and show the results now. */
  readonly seeResults = output<void>();

  protected readonly cardInfo = CARD_INFO;
  protected readonly eventInfo = EVENT_INFO;

  /** The server's time now, ticking four times a second. */
  private readonly now = signal(Date.now());

  constructor() {
    const tick = setInterval(() => this.now.set(Date.now()), 250);
    inject(DestroyRef).onDestroy(() => clearInterval(tick));
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
    return this.phase() === 'reveal' && !!round && this.dismissedReveal() !== round.round;
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

  protected readonly usedAbility = computed(() => {
    const used = this.board().ability;
    const mine = this.you()?.ability;
    return used ? (mine?.id === used ? mine.name : used) : null;
  });

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
