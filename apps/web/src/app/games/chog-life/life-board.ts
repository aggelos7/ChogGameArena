import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, input, output } from '@angular/core';
import { AvatarBadge } from '../../profile/avatar-badge';
import { Avatar } from '../../profile/profile-api';
import { RoomCharacter, RoomMember } from '../../rooms/rooms-api';
import { LifeBoard, LifeRoute, LifeTile, laneSummary, laneTiles, lifeRows, seatColor, tileLabel } from './life-board-model';

/** NFT Chogs have no colour of their own here; they use the brand colour. */
const NFT_COLOR = 'var(--brand)';

/** A pawn as drawn: its seat, colour and the player it belongs to. */
interface DrawnPawn {
  seat: number;
  color: string;
  name: string;
  isYou: boolean;
  isTurn: boolean;
  /** The route chosen at the fork it stands on. */
  lane?: LifeRoute;
}

/** A route offered at a fork, with what its tiles hold. */
interface RouteOption {
  route: LifeRoute;
  name: string;
  summary: string;
}

const ROUTE_NAMES: Record<LifeRoute, string> = { Safe: 'safe', Degen: 'degen' };

/**
 * The Chog Life board (STORY-036, DEC-038): a vertical track from start to finish, with each fork's Safe and Degen lanes
 * side by side, every tile's type and the pawns standing on it. Pawns are numbered and coloured by seat, and the legend
 * names each player and whose turn it is. It only draws what the server sent; it has no moves of its own. On your turn it
 * offers Roll, which the page sends to the server (STORY-037, DEC-039); the server's roll and move come back in the next
 * snapshot, shown as the last move with its destination marked and what the tile did to their MON. The legend shows
 * every player's liquid MON (STORY-038, DEC-040). A roll that stops on a fork keeps the turn: the player gets the safe
 * and degen routes with what each lane holds, highlighted on the board, and the page sends the choice (STORY-039,
 * DEC-041); everyone else sees who is choosing, and then which route they took.
 */
@Component({
  selector: 'app-life-board',
  imports: [AvatarBadge, NgTemplateOutlet],
  templateUrl: './life-board.html',
  styleUrl: './life-board.scss',
})
export class LifeBoardView {
  readonly board = input.required<LifeBoard>();
  readonly members = input.required<RoomMember[]>();
  /** A roll or a lane choice is on its way to the server. */
  readonly rolling = input(false);
  readonly roll = output<void>();
  readonly chooseLane = output<LifeRoute>();

  protected readonly label = tileLabel;
  protected readonly rows = computed(() => lifeRows(this.board().tiles));

  /** Players with a pawn on the board, by seat. */
  protected readonly players = computed(() => {
    const onBoard = new Set(this.board().pawns.map((pawn) => pawn.seat));
    return this.members()
      .filter((member): member is RoomMember & { seat: number } => member.seat !== null && onBoard.has(member.seat))
      .sort((a, b) => a.seat - b.seat);
  });

  /** The pawns standing on each tile, in seat order. */
  protected readonly pawnsByTile = computed(() => {
    const bySeat = new Map(this.players().map((member) => [member.seat, member]));
    const byTile = new Map<number, DrawnPawn[]>();
    for (const pawn of [...this.board().pawns].sort((a, b) => a.seat - b.seat)) {
      const member = bySeat.get(pawn.seat);
      const drawn: DrawnPawn = {
        seat: pawn.seat,
        color: seatColor(pawn.seat),
        name: member?.displayName ?? `Seat ${pawn.seat}`,
        isYou: !!member?.isYou,
        isTurn: !!member?.isTurn,
        lane: pawn.lane,
      };
      byTile.set(pawn.tile, [...(byTile.get(pawn.tile) ?? []), drawn]);
    }
    return byTile;
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

  /** The two routes on offer, with what each lane holds; only for the player choosing. */
  protected readonly routes = computed<RouteOption[]>(() => {
    const fork = this.fork();
    if (!this.chooser()?.isYou || fork === null) {
      return [];
    }
    return (['Safe', 'Degen'] as const).map((route) => ({
      route,
      name: ROUTE_NAMES[route],
      summary: laneSummary(laneTiles(this.board().tiles, fork, route)),
    }));
  });

  protected readonly defaultRoute = computed(() => ROUTE_NAMES[this.board().defaultLane ?? 'Safe']);

  /** The tiles of both lanes of the fork being chosen at, which the board highlights. */
  private readonly choiceTiles = computed(() => {
    const fork = this.fork();
    const tiles = this.board().tiles;
    return new Set(fork === null ? [] : [...laneTiles(tiles, fork, 'Safe'), ...laneTiles(tiles, fork, 'Degen')].map((tile) => tile.index));
  });

  /** Each player's liquid MON, by seat. */
  protected readonly monBySeat = computed(() => new Map(this.board().pawns.map((pawn) => [pawn.seat, pawn.mon])));

  /** What the latest move was, in words: who rolled what, where they went and what the tile did to their MON. */
  protected readonly lastMove = computed(() => {
    const move = this.board().lastMove;
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
    const route = !move.lane
      ? ''
      : move.byDefault
        ? ` Time ran out, so ${member?.isYou ? 'you take' : `${who} takes`} the ${ROUTE_NAMES[move.lane]} route.`
        : ` ${who} chose the ${ROUTE_NAMES[move.lane]} route.`;
    if (move.change === undefined) {
      return `${who} rolled ${move.roll} and moved to ${where}.${route}`;
    }
    const outcome =
      move.change > 0
        ? `won ${move.change} MON`
        : move.change < 0
          ? `lost ${-move.change} MON`
          : `${member?.isYou ? 'your' : 'their'} MON didn’t change`;
    return `${who} rolled ${move.roll}, landed on ${where} and ${outcome}.${route}`;
  });

  protected isChoice(tile: LifeTile): boolean {
    return this.choiceTiles().has(tile.index);
  }

  protected isLastStop(tile: LifeTile): boolean {
    const move = this.board().lastMove;
    return !!move && move.roll !== undefined && move.to === tile.index;
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

  protected badge(character: RoomCharacter): Avatar {
    return { id: character.id, name: character.name, color: character.color ?? NFT_COLOR };
  }
}
