import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, input, output } from '@angular/core';
import { AvatarBadge } from '../../profile/avatar-badge';
import { Avatar } from '../../profile/profile-api';
import { RoomCharacter, RoomMember } from '../../rooms/rooms-api';
import { LifeBoard, LifeTile, lifeRows, seatColor, tileLabel } from './life-board-model';

/** NFT Chogs have no colour of their own here; they use the brand colour. */
const NFT_COLOR = 'var(--brand)';

/** A pawn as drawn: its seat, colour and the player it belongs to. */
interface DrawnPawn {
  seat: number;
  color: string;
  name: string;
  isYou: boolean;
  isTurn: boolean;
}

/**
 * The Chog Life board (STORY-036, DEC-038): a vertical track from start to finish, with each fork's Safe and Degen lanes
 * side by side, every tile's type and the pawns standing on it. Pawns are numbered and coloured by seat, and the legend
 * names each player and whose turn it is. It only draws what the server sent; it has no moves of its own. On your turn it
 * offers Roll, which the page sends to the server (STORY-037, DEC-039); the server's roll and move come back in the next
 * snapshot, shown as the last move with its destination marked.
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
  /** A roll is on its way to the server. */
  readonly rolling = input(false);
  readonly roll = output<void>();

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
      };
      byTile.set(pawn.tile, [...(byTile.get(pawn.tile) ?? []), drawn]);
    }
    return byTile;
  });

  protected readonly turnOf = computed(() => this.players().find((member) => member.isTurn) ?? null);

  /** What the latest move was, in words: who rolled what and where they went. */
  protected readonly lastMove = computed(() => {
    const move = this.board().lastMove;
    if (!move) {
      return null;
    }
    const member = this.players().find((player) => player.seat === move.seat);
    const who = member?.isYou ? 'You' : (member?.displayName ?? `Seat ${move.seat}`);
    const tile = this.board().tiles[move.to];
    const where = tile ? `tile ${move.to}${this.label(tile) ? ` (${this.label(tile)})` : ''}` : `tile ${move.to}`;
    return move.roll === undefined ? `${who} passed at the finish.` : `${who} rolled ${move.roll} and moved to ${where}.`;
  });

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
      what.push(pawns.map((pawn) => (pawn.isYou ? `${pawn.name} (you)` : pawn.name)).join(', '));
    }
    return what.join(', ');
  }

  protected badge(character: RoomCharacter): Avatar {
    return { id: character.id, name: character.name, color: character.color ?? NFT_COLOR };
  }
}
