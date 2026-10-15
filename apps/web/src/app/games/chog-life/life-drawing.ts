import { Component, input } from '@angular/core';
import { LifeEventKind, LifeLayout, LifeTile, LifeTileType, SPOT_HEIGHT, SPOT_WIDTH, spotLabel } from './life-board-model';

/** A pawn as the illustrated board draws it: its seat and colour, and where it stands in board units. */
export interface DrawingPawn {
  seat: number;
  color: string;
  isYou: boolean;
  isTurn: boolean;
  /** The tile it stands on, or has hopped to. */
  tile: number;
  x: number;
  y: number;
}

/** A tile icon drawn in a 20 × 20 box around its centre: filled shapes and outlines. */
interface Icon {
  fill?: string;
  stroke?: string;
}

const COIN = 'M-8 0a8 8 0 1 0 16 0a8 8 0 1 0 -16 0';

/** Icons by tile type, and by event kind for event tiles (DEC-048). */
const TYPE_ICONS: Record<LifeTileType, Icon> = {
  Start: { fill: 'M-5 -8L8 0L-5 8Z' },
  Normal: { fill: 'M-3 0a3 3 0 1 0 6 0a3 3 0 1 0 -6 0' },
  Reward: { stroke: `${COIN} M0 -4V4 M-4 0H4` },
  Loss: { stroke: `${COIN} M-4 0H4` },
  Event: { stroke: 'M0 -8V3 M0 7V8' },
  Branch: { stroke: 'M0 9V2 M0 2L-7 -6 M0 2L7 -6 M-7 -6h5 M-7 -6v5 M7 -6h-5 M7 -6v5' },
  Finish: { fill: 'M-6 -9h4v4h-4z M2 -9h4v4h-4z M-2 -5h4v4h-4z M6 -5h2v4h-2z', stroke: 'M-6 -9V9 M-6 -9H8V-1H-6' },
};
const EVENT_ICONS: Record<LifeEventKind, Icon> = {
  Market: { stroke: 'M-9 7L-3 0L1 4L9 -6 M4 -6H9V-1' },
  Airdrop: { fill: 'M-3 6h6v4h-6z', stroke: 'M-9 -2a9 7 0 0 1 18 0Z M-9 -2L-1 6 M9 -2L1 6 M0 -2V6' },
  Rug: { stroke: 'M-8 -7h16v10h-16z M-5 -2h10 M-6 3v4 M-2 3v4 M2 3v4 M6 3v4' },
  Liquidation: { fill: 'M2 -10L-6 2H0L-2 10L6 -2H0Z' },
};

/**
 * The illustrated Chog Life board (STORY-064, DEC-048): the tiles where the layout put them, with an icon by type and
 * event kind, the roads between them coloured by lane, each fork's lane names, and the pawns. Pawns glide between the
 * places they're given, so a pawn handed each tile of its route in turn hops along it. It only draws: it's hidden from
 * screen readers, which read the board's list instead.
 */
@Component({
  selector: 'app-life-drawing',
  templateUrl: './life-drawing.html',
  styleUrl: './life-drawing.scss',
})
export class LifeDrawing {
  readonly layout = input.required<LifeLayout>();
  readonly pawns = input.required<DrawingPawn[]>();
  /** The tile the latest roll stopped on; null for none. */
  readonly lastStop = input<number | null>(null);
  /** The tiles of the lanes being chosen between at a fork. */
  readonly choice = input<ReadonlySet<number>>(new Set());

  protected readonly spotWidth = SPOT_WIDTH;
  protected readonly spotHeight = SPOT_HEIGHT;
  protected readonly label = spotLabel;

  protected icon(tile: LifeTile): Icon {
    return tile.type === 'Event' && tile.event ? EVENT_ICONS[tile.event] : TYPE_ICONS[tile.type];
  }
}
