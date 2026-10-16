import { Component, DestroyRef, ElementRef, afterNextRender, computed, inject, input, output, signal } from '@angular/core';
import { AvatarBadge } from '../../profile/avatar-badge';
import { Avatar } from '../../profile/profile-api';
import { RoomCharacter, RoomMember } from '../../rooms/rooms-api';
import { seatColor } from '../seats';
import { RACE_CARD_NAMES, RaceBoard, RaceCard, RaceMove, RaceTile, raceColumns, raceLayout, raceTileLabel } from './race-board-model';

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
 * each player and whose turn it is. It only draws what the server sent. On your turn it offers Roll in the Move phase and
 * End turn in the Play phase (STORY-051, DEC-055), and says what the latest move did; the server decides every outcome.
 * It shows your hand, how many cards each player holds and what's left in the deck and discard pile (STORY-052, DEC-056);
 * the server only ever sends your own cards.
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
  /** A command is on its way: the buttons wait for it. */
  readonly rolling = input(false);
  readonly roll = output<void>();
  readonly endTurn = output<void>();

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

  /** How many cards each seat holds. */
  protected readonly cardCounts = computed(() => new Map(this.board().pawns.map((pawn) => [pawn.seat, pawn.cards ?? 0])));

  /** Your cards by name, while you're playing; null when the server sent no hand. */
  protected readonly hand = computed(() => this.board().hand?.map((card) => this.cardName(card)) ?? null);

  protected readonly turnOf = computed(() => this.players().find((member) => member.isTurn) ?? null);

  /** Your turn's phase, while it's your turn in a live match; null otherwise. */
  protected readonly yourPhase = computed(() => (this.turnOf()?.isYou ? (this.board().phase ?? null) : null));

  /** What the latest move did, in words: who rolled what, where they landed and where the tile sent them. */
  protected readonly lastMove = computed(() => {
    const move = this.board().lastMove;
    if (!move) {
      return null;
    }
    const member = this.members().find((player) => player.seat === move.seat);
    const who = member?.isYou ? 'You' : (member?.displayName ?? `Seat ${move.seat}`);
    const rolled = `${who} rolled ${move.roll}`;
    switch (move.effect) {
      case 'Boost':
        return `${rolled}, landed on the Boost at tile ${move.landed} and jumped to tile ${move.to}.`;
      case 'Setback':
        return `${rolled}, landed on the Setback at tile ${move.landed} and fell back to tile ${move.to}.`;
      case 'Card':
        return move.draw
          ? `${rolled}, landed on the Card tile ${move.to}${this.drew(move, !!member?.isYou)}.`
          : `${rolled} and landed on the Card tile ${move.to}.`;
      case 'Finish':
        return this.board().winner === move.seat
          ? `${rolled}, reached the finish and won the race!`
          : `${rolled} and reached the finish.`;
      default:
        return `${rolled} and moved to tile ${move.to}.`;
    }
  });

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

  /** What a Card tile drew, as the end of the last-move sentence: the card by name only for the player who drew it. */
  private drew(move: RaceMove, isYou: boolean): string {
    switch (move.draw) {
      case 'Drawn': {
        const card = move.card ? this.cardName(move.card) : 'a card';
        const reshuffled = move.reshuffled ? ' after the discard pile was shuffled into a new deck' : '';
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
