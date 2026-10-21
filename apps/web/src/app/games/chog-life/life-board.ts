import { NgTemplateOutlet } from '@angular/common';
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
import { DrawingPawn, LifeDrawing } from './life-drawing';
import {
  FORK_ABILITIES,
  LifeBoard,
  LifeMove,
  LifePawn,
  LifeRoute,
  LifeTax,
  LifeTile,
  diceList,
  laneSummary,
  laneTiles,
  fittedLayout,
  lifeLayout,
  lifeRows,
  ordinal,
  pawnOffset,
  previewSummary,
  seatColor,
  signedMon,
  stakeSummary,
  takesMon,
  tileLabel,
  timeLeft,
} from './life-board-model';
import { HolderBadge } from '../holder-badge';
import { ReportPhoto } from '../../moderation/report-photo';
import { DIE_MS, DieRoll, RollingDie } from '../rolling-die';
import { BANNER_MS, MoveBanner, MoveNews, RECENT_MOVES, RecentMoves, signedMonText } from '../move-news';

/** A pawn as drawn: its seat, colour and the player it belongs to. */
interface DrawnPawn {
  seat: number;
  color: string;
  /** The player's profile photo, drawn as the pawn inside a ring of the seat colour (STORY-062); null for a numbered pawn. */
  imageUrl: string | null;
  name: string;
  isYou: boolean;
  isTurn: boolean;
  /** The route chosen at the fork it stands on. */
  lane?: LifeRoute;
}

/** A route offered at a fork, with what its tiles hold and what staking on it returns. */
interface RouteOption {
  route: LifeRoute;
  name: string;
  summary: string;
  stake: string | null;
}

const ROUTE_NAMES: Record<LifeRoute, string> = { Safe: 'safe', Degen: 'degen' };

/** A pawn on the illustrated board, where it's drawn: on its tile, or on the way there while it hops. */
interface BoardPawn extends DrawnPawn, DrawingPawn {}

/** How long a pawn takes to hop one tile when a move is shown (DEC-048). */
export const HOP_MS = 200;

/** The board's width from which the illustrated board runs across the screen instead of down it (DEC-048). */
export const WIDE_BOARD = 760;

/** The board's width from which the players sit beside the board, which then takes the height left on screen (DEC-048). */
export const SIDE_BY_SIDE = 1200;

/** The least height the board is given beside the players, however little is left on screen. */
const LEAST_HEIGHT = 360;

/** Where this device keeps the board view its player chose. */
const VIEW_KEY = 'chogLife.boardView';

type BoardView = 'drawing' | 'list';

function savedView(): BoardView {
  try {
    return globalThis.localStorage?.getItem(VIEW_KEY) === 'list' ? 'list' : 'drawing';
  } catch {
    return 'drawing';
  }
}

export function reducedMotion(): boolean {
  return typeof globalThis.matchMedia === 'function' && globalThis.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

/**
 * The Chog Life board (STORY-036, DEC-038): a vertical track from start to finish, with each fork's Safe and Degen lanes
 * side by side, every tile's type and the pawns standing on it. Pawns are numbered and coloured by seat, and the legend
 * names each player and whose turn it is. It only draws what the server sent; it has no moves of its own. On your turn it
 * offers Roll, which the page sends to the server (STORY-037, DEC-039); the server's roll and move come back in the next
 * snapshot, shown as the last move with its destination marked and what the tile did to their MON. The legend shows
 * every player's portfolio as the server values it: the total, then liquid MON, assets, bonuses and debt (STORY-038,
 * STORY-040, DEC-040, DEC-042). A roll that stops on a fork keeps the turn: the player gets the safe and degen routes
 * with what each lane holds and what staking on it returns, highlighted on the board, and the page sends the choice
 * (STORY-039, DEC-041); everyone else sees who is choosing, and then which route they took and what they staked.
 * While the match runs it counts down the time left to the server's `endsAt`; the legend marks who has finished and in
 * which place, and once the match is over it says what ended it, over the final portfolios (STORY-041, DEC-043).
 * On your turn it offers your locked ability while it has uses left and can be used now: Reward Boost, Loss Protection
 * and Lucky Reroll before you roll, Event Preview at a fork before you choose a route; the legend marks an armed
 * ability and the last move says what it did (for Lucky Reroll, both outcomes drawn). After Event Preview, choosing a
 * route shows only you your next move there, and you keep that route or switch to the other one blind (STORY-042,
 * DEC-044).
 * The track is drawn as an illustrated board laid out from the server's tile graph: a winding road in rows, with each
 * fork's lanes side by side, tile icons by type and event kind, and pawns that hop along the route of each new move. The
 * vertical track stays as the list view, for screen readers, on narrow screens and when the graph can't be laid out.
 * From a 760 px width the board runs across the screen, and from 1200 px the players sit beside it with the controls
 * under it, so the whole game fits a 1080p screen (STORY-064, DEC-048).
 */
@Component({
  selector: 'app-life-board',
  imports: [LifeDrawing, NgTemplateOutlet, ReportPhoto, RollingDie, MoveBanner, RecentMoves, HolderBadge],
  templateUrl: './life-board.html',
  styleUrl: './life-board.scss',
})
export class LifeBoardView {
  readonly board = input.required<LifeBoard>();
  readonly members = input.required<RoomMember[]>();
  /** When the match's time is up (ISO 8601), counted down while it runs; null for no clock. */
  readonly endsAt = input<string | null>(null);
  /** A roll or a lane choice is on its way to the server. */
  readonly rolling = input(false);
  readonly roll = output<void>();
  /** The move on the board has been shown: its die and hops are over, or it had none (DEC-076). */
  readonly settled = output<void>();
  readonly chooseLane = output<LifeRoute>();
  /** Uses your locked ability, by id. */
  readonly useAbility = output<string>();
  /** Rolls keeping one of the dice Double Dice rolled for you (STORY-091, DEC-078). */
  readonly keepDie = output<number>();

  protected readonly label = tileLabel;
  protected readonly takesMon = takesMon;
  protected readonly ordinal = ordinal;

  /** The time now, ticking every second for the clock. */
  private readonly now = signal(Date.now());

  /** The illustrated board, or null when the tile graph can't be laid out and only the list is shown. */
  protected readonly layout = computed(() => {
    const tiles = this.board().tiles;
    const space = this.space();
    if (space) {
      return fittedLayout(tiles, space.width, space.height);
    }
    return lifeLayout(tiles, this.width() >= WIDE_BOARD ? 'horizontal' : 'vertical');
  });
  /** The board's width, once measured. */
  private readonly width = signal(0);
  /** Beside the players: the width of the board and the height left for it on screen, in pixels; null otherwise. */
  private readonly space = signal<{ width: number; height: number } | null>(null);
  private readonly box = viewChild<ElementRef<HTMLElement>>('box');
  /** The drawing's height beside the players: the space left, or less when the board is wider than it is tall. */
  protected readonly fitHeight = computed(() => {
    const space = this.space();
    const plan = this.layout();
    return space && plan ? Math.min(space.height, (space.width * plan.height) / plan.width) : null;
  });
  protected readonly view = signal<BoardView>(savedView());

  /** The pawn hopping along the latest move and the tile it's on now; null when no move is being shown. */
  private readonly hop = signal<{ seat: number; tile: number } | null>(null);
  /** The die over the board while a new roll is shown, before its pawn hops (DEC-076). */
  protected readonly die = signal<DieRoll | null>(null);
  private rolls = 0;
  /** The latest move already seen; undefined before the first board, whose move is never replayed. */
  private seenMove: string | null | undefined = undefined;
  private hopTimer: ReturnType<typeof setTimeout> | undefined;
  /** A move's die or hops are being shown (DEC-076). */
  readonly moving = signal(false);
  /**
   * The latest move as the page tells it: the server's, once its die and hops have been shown, so the words don't give
   * the roll away before the die lands (DEC-076).
   */
  private readonly revealed = signal<LifeMove | undefined>(undefined);
  /** What the move just shown did, over the board for a moment; null otherwise. */
  protected readonly banner = signal<MoveNews | null>(null);
  /** The popup over the tile the move just shown stopped on, while its banner shows. */
  protected readonly popup = computed(() => {
    const news = this.banner();
    return news?.popup ? { ...news.popup, tone: news.tone, id: news.id } : null;
  });
  /** The latest moves seen on this page, newest first. */
  protected readonly recent = signal<MoveNews[]>([]);
  private news = 0;
  private bannerTimer: ReturnType<typeof setTimeout> | undefined;

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
    // Beside the players, the board takes the height left below its top; measured after each render, as the page moves.
    afterEveryRender(() => this.measure());
    const onResize = (): void => this.measure();
    globalThis.addEventListener?.('resize', onResize);
    inject(DestroyRef).onDestroy(() => {
      clearInterval(tick);
      clearTimeout(this.hopTimer);
      clearTimeout(this.bannerTimer);
      resize?.disconnect();
      globalThis.removeEventListener?.('resize', onResize);
    });
    effect(() => {
      const move = this.board().lastMove;
      untracked(() => this.follow(move));
    });
  }

  private measure(): void {
    const box = this.box()?.nativeElement;
    let space: { width: number; height: number } | null = null;
    const rect = box?.getBoundingClientRect();
    if (rect && rect.width > 0 && this.width() >= SIDE_BY_SIDE) {
      const top = rect.top + (globalThis.scrollY ?? 0);
      space = { width: Math.round(rect.width), height: Math.round(Math.max(LEAST_HEIGHT, (globalThis.innerHeight ?? 0) - top - 16)) };
    }
    const current = this.space();
    if (space?.width !== current?.width || space?.height !== current?.height) {
      this.space.set(space);
    }
  }

  protected toggleView(): void {
    const view: BoardView = this.view() === 'drawing' ? 'list' : 'drawing';
    this.view.set(view);
    try {
      globalThis.localStorage?.setItem(VIEW_KEY, view);
    } catch {
      // Without storage the choice lasts until the page closes.
    }
  }

  /**
   * Shows a new move: the die tumbles and lands on the roll while its pawn waits where it was (DEC-076), then the pawn hops
   * along the route the server resolved, a tile at a time, and stands where the server put it; then what it did is told
   * (a banner, a popup, Recent moves and the last-move line). The first board's move, a pass at the finish and reduced
   * motion show neither die nor hops, and the first board's move no banner.
   */
  private follow(move: LifeMove | undefined): void {
    const key = move ? JSON.stringify([move.seat, move.roll, move.from, move.to, move.path]) : null;
    if (key === this.seenMove) {
      if (!this.moving()) {
        // The same move told again, as after a route choice: its words follow.
        this.revealed.set(move);
        this.settled.emit();
      }
      return;
    }
    const first = this.seenMove === undefined;
    this.seenMove = key;
    clearTimeout(this.hopTimer);
    this.hop.set(null);
    this.die.set(null);
    if (first || !move || move.roll === undefined || move.path.length === 0 || reducedMotion()) {
      this.moving.set(false);
      this.reveal(move, !first);
      this.settled.emit();
      return;
    }
    this.moving.set(true);
    const route = [move.from, ...move.path];
    let step = 0;
    const next = (): void => {
      this.die.set(null);
      this.hop.set(step < route.length ? { seat: move.seat, tile: route[step++] } : null);
      if (this.hop()) {
        this.hopTimer = setTimeout(next, HOP_MS);
      } else {
        this.moving.set(false);
        this.reveal(this.board().lastMove ?? move, true);
        this.settled.emit();
      }
    };
    this.die.set({ value: move.roll, id: ++this.rolls });
    this.hop.set({ seat: move.seat, tile: move.from });
    this.hopTimer = setTimeout(next, DIE_MS);
  }

  /** Tells a move that has been shown: the last-move line, Recent moves and, when `announce`, the banner and popup. */
  private reveal(move: LifeMove | undefined, announce: boolean): void {
    this.revealed.set(move);
    const news = move ? this.newsOf(move) : null;
    if (!news) {
      return;
    }
    this.recent.update((recent) => [news, ...recent].slice(0, RECENT_MOVES));
    if (announce) {
      clearTimeout(this.bannerTimer);
      this.banner.set(news);
      this.bannerTimer = setTimeout(() => this.banner.set(null), BANNER_MS);
    }
  }

  /** A roll's news: who rolled what, and what the tile did; null for a pass at the finish. */
  private newsOf(move: LifeMove): MoveNews | null {
    if (move.roll === undefined) {
      return null;
    }
    const member = this.players().find((player) => player.seat === move.seat);
    const who = member?.isYou ? 'You' : (member?.displayName ?? `Seat ${move.seat}`);
    const tile = this.board().tiles[move.to];
    const news = { id: ++this.news, title: `${who} rolled ${move.roll}` };
    if (move.place !== undefined) {
      const bonus = move.bonus ? ` ${signedMonText(move.bonus)} bonus` : '';
      return { ...news, detail: `Finished ${ordinal(move.place)}!${bonus}`, tone: 'good', popup: { tile: move.to, text: `${ordinal(move.place)}!` } };
    }
    if (move.change !== undefined && move.change !== 0) {
      const name = tile?.type === 'Event' ? `${tileLabel(tile)}!` : move.change > 0 ? 'Reward!' : 'Loss!';
      const change = signedMonText(move.change);
      return { ...news, detail: `${name} ${change}`, tone: move.change > 0 ? 'good' : 'bad', popup: { tile: move.to, text: change } };
    }
    if (move.matured) {
      const { stake, payout } = move.matured;
      return {
        ...news,
        detail: `Stake paid back ${payout} MON`,
        tone: payout >= stake ? 'good' : 'bad',
        popup: { tile: move.to, text: signedMonText(payout) },
      };
    }
    const where = tile?.type === 'Branch' ? 'a fork: pick a route!' : `tile ${move.to}`;
    return { ...news, detail: `Moved to ${where}`, tone: 'neutral', popup: null };
  }

  /** The time left as "12:05", while the match runs and has a time limit. */
  protected readonly clock = computed(() => {
    const endsAt = this.endsAt();
    const end = endsAt ? Date.parse(endsAt) : NaN;
    return Number.isNaN(end) || this.board().ended ? null : timeLeft(end, this.now());
  });

  /** What ended the match, in words, once it's over. */
  protected readonly ending = computed(() => {
    switch (this.board().ended) {
      case 'TimeUp':
        return 'Time’s up! Players are ranked by their final portfolio.';
      case 'Finish':
        return 'Everyone reached the finish! Players are ranked by their final portfolio.';
      default:
        return null;
    }
  });
  protected readonly rows = computed(() => lifeRows(this.board().tiles));

  /** Players with a pawn on the board, by seat. */
  protected readonly players = computed(() => {
    const onBoard = new Set(this.board().pawns.map((pawn) => pawn.seat));
    return this.members()
      .filter((member): member is RoomMember & { seat: number } => member.seat !== null && onBoard.has(member.seat))
      .sort((a, b) => a.seat - b.seat);
  });

  /** Every pawn as drawn, in seat order. */
  private readonly drawnPawns = computed(() => {
    const bySeat = new Map(this.players().map((member) => [member.seat, member]));
    return [...this.board().pawns]
      .sort((a, b) => a.seat - b.seat)
      .map((pawn): DrawnPawn & { tile: number } => {
        const member = bySeat.get(pawn.seat);
        return {
          seat: pawn.seat,
          tile: pawn.tile,
          color: seatColor(pawn.seat),
          imageUrl: member?.avatarImageUrl ?? null,
          name: member?.displayName ?? `Seat ${pawn.seat}`,
          isYou: !!member?.isYou,
          isTurn: !!member?.isTurn,
          lane: pawn.lane,
        };
      });
  });

  /** The pawns standing on each tile, in seat order, where the server put them. */
  protected readonly pawnsByTile = computed(() => {
    const byTile = new Map<number, DrawnPawn[]>();
    for (const pawn of this.drawnPawns()) {
      byTile.set(pawn.tile, [...(byTile.get(pawn.tile) ?? []), pawn]);
    }
    return byTile;
  });

  /** The pawns on the illustrated board, sharing their tile's edges, with a hopping pawn on the tile it has reached. */
  protected readonly boardPawns = computed<BoardPawn[]>(() => {
    const spots = this.layout()?.spots;
    if (!spots) {
      return [];
    }
    const hop = this.hop();
    const pawns = this.drawnPawns().map((pawn) => (hop?.seat === pawn.seat ? { ...pawn, tile: hop.tile } : pawn));
    return pawns.map((pawn) => {
      const together = pawns.filter((other) => other.tile === pawn.tile);
      const offset = pawnOffset(together.indexOf(pawn), together.length);
      return { ...pawn, x: spots[pawn.tile].x + offset.x, y: spots[pawn.tile].y + offset.y };
    });
  });

  protected readonly turnOf = computed(() => this.players().find((member) => member.isTurn) ?? null);

  /** The player whose roll stopped on a fork and who is choosing a route, while it's still their turn. */
  protected readonly chooser = computed(() => {
    const seat = this.board().choosing;
    return this.players().find((member) => member.seat === seat && member.isTurn) ?? null;
  });

  /** The fork the chooser stands on. */
  private readonly fork = computed(() => {
    const seat = this.chooser()?.seat;
    return this.board().pawns.find((pawn) => pawn.seat === seat)?.tile ?? null;
  });

  /** The two routes on offer, with what each lane holds and what staking on it returns; only for the player choosing. */
  protected readonly routes = computed<RouteOption[]>(() => {
    const fork = this.fork();
    const board = this.board();
    const you = board.pawns.find((pawn) => pawn.seat === this.chooser()?.seat);
    if (!this.chooser()?.isYou || fork === null || !you) {
      return [];
    }
    // The server stakes the fork stake, or all the liquid MON held if less, unless a stake is already held (DEC-042).
    const stake = board.forkStake && !you.stake ? Math.min(board.forkStake, you.mon) : 0;
    return (['Safe', 'Degen'] as const).map((route) => ({
      route,
      name: ROUTE_NAMES[route],
      summary: laneSummary(laneTiles(board.tiles, fork, route)),
      stake: board.forkStake ? stakeSummary(stake, board.stakeReturns?.find((returns) => returns.lane === route)) : null,
    }));
  });

  /** Your locked ability, while it has uses left and it's your turn with your pawn still on the board. */
  private readonly yourAbility = computed(() => {
    const you = this.players().find((member) => member.isYou);
    const pawn = you ? this.pawnBySeat().get(you.seat) : undefined;
    const ability = you?.ability;
    if (!you?.isTurn || !pawn || pawn.place !== undefined || this.board().ended || !ability || !ability.usesLeft) {
      return null;
    }
    return { ability, pawn };
  });

  /** Your ability, when it can be used before you roll: not while choosing a route, nor while one is armed. */
  protected readonly rollAbility = computed(() => {
    const yours = this.yourAbility();
    return yours && !this.chooser() && !FORK_ABILITIES.includes(yours.ability.id) && !yours.pawn.armed ? yours.ability : null;
  });

  /** Your ability, when it can be used at a fork before you choose a route and isn't armed yet. */
  protected readonly forkAbility = computed(() => {
    const yours = this.yourAbility();
    return yours && this.chooser()?.isYou && FORK_ABILITIES.includes(yours.ability.id) && !yours.pawn.armed ? yours.ability : null;
  });

  /** You armed Event Preview at this fork: choosing a route will show your next move there. */
  protected readonly previewArmed = computed(() => {
    const you = this.players().find((member) => member.isYou);
    const armed = you ? this.pawnBySeat().get(you.seat)?.armed : undefined;
    return !!this.chooser()?.isYou && !!armed && FORK_ABILITIES.includes(armed);
  });

  /** The player who previewed their next move and is deciding whether to keep their route, while it's their turn. */
  protected readonly decider = computed(() => {
    const seat = this.board().confirming;
    return this.players().find((member) => member.seat === seat && member.isTurn) ?? null;
  });

  /** Your previewed move, in words, until you roll. */
  protected readonly previewText = computed(() => {
    const preview = this.board().preview;
    return preview ? previewSummary(preview, this.board().tiles) : null;
  });

  /** While you decide: the route you chose and the other one, to keep or switch to. */
  protected readonly keepOrSwitch = computed(() => {
    const preview = this.board().preview;
    if (!this.decider()?.isYou || !preview) {
      return null;
    }
    const other: LifeRoute = preview.lane === 'Safe' ? 'Degen' : 'Safe';
    return { keep: preview.lane, keepName: ROUTE_NAMES[preview.lane], other, otherName: ROUTE_NAMES[other] };
  });

  /** The dice Double Dice rolled for you to keep one of, distinct and in order, while it's your turn; empty otherwise. */
  protected readonly yourDice = computed(() => {
    const you = this.players().find((member) => member.isYou);
    const pawn = you?.isTurn ? this.pawnBySeat().get(you.seat) : undefined;
    return pawn?.dice && pawn.kept === undefined && !this.board().ended ? [...new Set(pawn.dice)].sort((a, b) => a - b) : [];
  });

  /**
   * Double Dice's dice waiting for a roll, in words, for everyone: "Your Double Dice rolled 2 and 5: keep one to move
   * by it." or "Alice’s Double Dice rolled 2 and 5." With a die kept when time ran out, which one the next roll moves by.
   */
  protected readonly diceNote = computed(() => {
    const pawn = this.board().pawns.find((each) => each.dice);
    if (!pawn?.dice || this.board().ended) {
      return null;
    }
    const member = this.players().find((player) => player.seat === pawn.seat);
    const whose = member?.isYou ? 'Your' : `${member?.displayName ?? `Seat ${pawn.seat}`}’s`;
    const rolled = `${whose} Double Dice rolled ${diceList(pawn.dice)}`;
    if (pawn.kept !== undefined) {
      return `${rolled}. Time ran out, so ${member?.isYou ? 'your' : 'their'} next roll moves by ${pawn.kept}.`;
    }
    return member?.isYou ? `${rolled}: keep one to move by it.` : `${rolled}.`;
  });

  /** A Tax the Rich used this turn, in words: "You took 30 MON from Bob with Tax the Rich." */
  protected readonly taxNote = computed(() => {
    const tax = this.board().tax;
    return tax ? `${this.taxed(tax)}.` : null;
  });

  /** Who took how much from whom with Tax the Rich: "Alice took 30 MON from you with Tax the Rich". */
  private taxed(tax: LifeTax): string {
    const name = (seat: number, you: string): string => {
      const member = this.players().find((player) => player.seat === seat);
      return member?.isYou ? you : (member?.displayName ?? `Seat ${seat}`);
    };
    return `${name(tax.seat, 'You')} took ${tax.amount} MON from ${name(tax.from, 'you')} with Tax the Rich`;
  }

  /** The name of the ability the player on `seat` has armed; null when none is. */
  protected armedName(seat: number): string | null {
    const armed = this.pawnBySeat().get(seat)?.armed;
    if (!armed) {
      return null;
    }
    const ability = this.players().find((member) => member.seat === seat)?.ability;
    return ability?.id === armed ? ability.name : armed;
  }

  protected readonly defaultRoute = computed(() => ROUTE_NAMES[this.board().defaultLane ?? 'Safe']);

  /** The tiles of both lanes of the fork being chosen at, which the board highlights. */
  protected readonly choiceTiles = computed(() => {
    const fork = this.fork();
    const tiles = this.board().tiles;
    return new Set(fork === null ? [] : [...laneTiles(tiles, fork, 'Safe'), ...laneTiles(tiles, fork, 'Degen')].map((tile) => tile.index));
  });

  /** Each player's pawn with their portfolio, by seat. */
  protected readonly pawnBySeat = computed(() => new Map<number, LifePawn>(this.board().pawns.map((pawn) => [pawn.seat, pawn])));

  /** What the latest move was, in words: who rolled what, where they went and what the tile did to their MON. */
  protected readonly lastMove = computed(() => {
    const move = this.revealed();
    if (!move) {
      return null;
    }
    const member = this.players().find((player) => player.seat === move.seat);
    const who = member?.isYou ? 'You' : (member?.displayName ?? `Seat ${move.seat}`);
    const tile = this.board().tiles[move.to];
    const where = tile ? `tile ${move.to}${this.label(tile) ? ` (${this.label(tile)})` : ''}` : `tile ${move.to}`;
    if (move.roll === undefined) {
      return `${who} passed at the finish.`;
    }
    // Abilities used before the roll come first: "Alice took 30 MON from you with Tax the Rich. Alice rolled 2 and 5
    // with Double Dice and kept 2, landed on…" (STORY-091, DEC-078).
    const taxed = move.taxed ? `${this.taxed(move.taxed)}. ` : '';
    const rolled = move.dice ? `rolled ${diceList(move.dice)} with Double Dice and kept ${move.roll}` : `rolled ${move.roll}`;
    if (move.place !== undefined) {
      const bonus = move.bonus ? `, winning the ${move.bonus} MON bonus` : '';
      return `${taxed}${who} ${rolled} and reached the finish in ${ordinal(move.place)} place${bonus}.`;
    }
    const staked = move.staked ? ` and ${member?.isYou ? 'stake' : 'stakes'} ${move.staked} MON` : '';
    const route = !move.lane
      ? ''
      : move.switched
        ? ` ${who} switched to the ${ROUTE_NAMES[move.lane]} route${move.staked ? `, taking the ${move.staked} MON stake along` : ''}.`
        : move.byDefault
        ? ` Time ran out, so ${member?.isYou ? 'you take' : `${who} takes`} the ${ROUTE_NAMES[move.lane]} route${staked}.`
        : ` ${who} chose the ${ROUTE_NAMES[move.lane]} route${move.staked ? ` and staked ${move.staked} MON` : ''}.`;
    const matured = move.matured
      ? ` ${member?.isYou ? 'Your' : 'Their'} ${ROUTE_NAMES[move.matured.lane]} stake of ${move.matured.stake} MON paid back ${move.matured.payout} MON.`
      : '';
    const yours = member?.isYou ? 'Your' : 'Their';
    const ability =
      move.boost !== undefined
        ? ` ${yours} Reward Boost added ${move.boost} MON to bonuses.`
        : move.saved !== undefined
          ? ` ${yours} Loss Protection saved ${move.saved} MON.`
          : move.rerolled !== undefined
            ? ` ${yours} Lucky Reroll drew the event ${timesDrawn(move.draws ?? 2)}: it kept ${signedMon(move.change ?? 0)} over ${signedMon(move.rerolled)}.`
            : '';
    if (move.change === undefined) {
      return `${taxed}${who} ${rolled} and moved to ${where}.${matured}${route}`;
    }
    const outcome =
      move.change > 0
        ? `won ${move.change} MON`
        : move.change < 0
          ? `lost ${-move.change} MON`
          : `${member?.isYou ? 'your' : 'their'} MON didn’t change`;
    return `${taxed}${who} ${rolled}, landed on ${where} and ${outcome}.${ability}${matured}${route}`;
  });

  protected isChoice(tile: LifeTile): boolean {
    return this.choiceTiles().has(tile.index);
  }

  /** The tile the latest roll stopped on, which the board marks; null before the first roll or after a pass. */
  protected readonly lastStop = computed(() => {
    const move = this.revealed();
    return move && move.roll !== undefined ? move.to : null;
  });

  protected isLastStop(tile: LifeTile): boolean {
    return this.lastStop() === tile.index;
  }

  protected pawnsOn(tile: LifeTile): DrawnPawn[] {
    return this.pawnsByTile().get(tile.index) ?? [];
  }

  protected color(seat: number): string {
    return seatColor(seat);
  }

  /** What a screen reader hears for a tile: its number, what it is, its lane and who stands on it. */
  protected describe(tile: LifeTile): string {
    const what = [`Tile ${tile.index}`, this.label(tile) || 'Normal'];
    if (tile.lane !== 'Main') {
      what.push(`${tile.lane} route`);
    }
    const pawns = this.pawnsOn(tile);
    if (pawns.length > 0) {
      what.push(
        pawns
          .map((pawn) => (pawn.isYou ? `${pawn.name} (you)` : pawn.name) + (pawn.lane ? `, taking the ${ROUTE_NAMES[pawn.lane]} route` : ''))
          .join(', '),
      );
    }
    return what.join(', ');
  }

}

/** How often Lucky Reroll drew: "twice", or "3 times" from a rarer Tier (STORY-090, DEC-077). */
function timesDrawn(draws: number): string {
  return draws === 2 ? 'twice' : `${draws} times`;
}
