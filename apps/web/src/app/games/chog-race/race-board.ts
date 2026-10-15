import { Component, DestroyRef, ElementRef, afterNextRender, computed, inject, input, signal } from '@angular/core';
import { AvatarBadge } from '../../profile/avatar-badge';
import { Avatar } from '../../profile/profile-api';
import { RoomCharacter, RoomMember } from '../../rooms/rooms-api';
import { seatColor } from '../seats';
import { RaceBoard, RaceTile, raceColumns, raceLayout, raceTileLabel } from './race-board-model';

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
 * The Chog Race board (STORY-050, DEC-054): the track as a serpentine grid, four tiles a row on a phone and up to ten on
 * wider screens, so the whole race is on screen. Each tile shows its number, what it does and the pawns on it, up to all
 * six. Pawns are numbered and coloured by seat, yours outlined and the current player's ringed, and the legend names
 * each player and whose turn it is. It only draws what the server sent; it has no moves of its own.
 */
@Component({
  selector: 'app-race-board',
  imports: [AvatarBadge],
  templateUrl: './race-board.html',
  styleUrl: './race-board.scss',
})
export class RaceBoardView {
  readonly board = input.required<RaceBoard>();
  readonly members = input.required<RoomMember[]>();

  protected readonly label = raceTileLabel;

  /** The board's width, once measured; until then, and without ResizeObserver, the phone layout. */
  private readonly width = signal(0);

  protected readonly columns = computed(() => raceColumns(this.width()));
  protected readonly spots = computed(() => raceLayout(this.board().tiles, this.columns()));

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

  constructor() {
    const host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
    let resize: ResizeObserver | undefined;
    afterNextRender(() => {
      if (typeof ResizeObserver === 'function') {
        resize = new ResizeObserver(([entry]) => this.width.set(entry.contentRect.width));
        resize.observe(host);
      }
    });
    inject(DestroyRef).onDestroy(() => resize?.disconnect());
  }

  protected pawnsOn(tile: RaceTile): DrawnPawn[] {
    return this.pawnsByTile().get(tile.index) ?? [];
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

  protected badge(character: RoomCharacter): Avatar {
    return { id: character.id, name: character.name, color: character.color ?? NFT_COLOR };
  }
}
