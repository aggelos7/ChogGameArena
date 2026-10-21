import { Component, input } from '@angular/core';

/** How long a move's banner stays over the board once its pawn has stopped (DEC-076). */
export const BANNER_MS = 2600;
/** How many moves the board's Recent moves list keeps. */
export const RECENT_MOVES = 5;

/** Whether a move was good for its player, bad, or neither: the colour of its banner, popup and Recent moves line. */
export type NewsTone = 'good' | 'bad' | 'neutral';

/**
 * What one move did, for the players to see at a glance (STORY-089, DEC-076): a banner over the board when its pawn
 * stops, a popup over the tile it stopped on, and a line in Recent moves. Built by each board from the server's move.
 */
export interface MoveNews {
  /** New for each move, so the same news twice shows twice. */
  id: number;
  /** Who did what: "Alice rolled 4". */
  title: string;
  /** What came of it: "Airdrop! +25 MON". */
  detail: string;
  tone: NewsTone;
  /** The tile the popup floats up from, and what it says ("+25 MON"); null for no popup. */
  popup: { tile: number; text: string } | null;
}

/** Signed MON for a banner or popup: "+25 MON", "−40 MON". */
export function signedMonText(mon: number): string {
  return `${mon > 0 ? '+' : mon < 0 ? '−' : ''}${Math.abs(mon)} MON`;
}

/** Signed tiles for a banner or popup: "+2 tiles", "−1 tile". */
export function signedTilesText(tiles: number): string {
  return `${tiles > 0 ? '+' : tiles < 0 ? '−' : ''}${Math.abs(tiles)} ${Math.abs(tiles) === 1 ? 'tile' : 'tiles'}`;
}

/**
 * The banner over a board for the move just shown: who did what, and what came of it, coloured by tone. A picture of the
 * board's last-move line, which tells screen readers the same.
 */
@Component({
  selector: 'app-move-banner',
  template: `
    @if (news(); as shown) {
      <div class="banner-layer" aria-hidden="true">
        <div [class]="'banner ' + shown.tone" [attr.data-banner]="shown.tone">
          <strong data-banner-title>{{ shown.title }}</strong>
          <span data-banner-detail>{{ shown.detail }}</span>
        </div>
      </div>
    }
  `,
  styleUrl: './move-news.scss',
})
export class MoveBanner {
  readonly news = input<MoveNews | null>(null);
}

/** The board's latest moves, newest first, each coloured by how it went for its player (DEC-076). */
@Component({
  selector: 'app-recent-moves',
  template: `
    @if (moves().length > 0) {
      <section class="recent" aria-labelledby="recent-moves-heading" data-recent-moves>
        <h3 id="recent-moves-heading">Recent moves</h3>
        <ol>
          @for (move of moves(); track move.id) {
            <li [class]="move.tone" [attr.data-tone]="move.tone">
              <span class="mark" aria-hidden="true">{{ move.tone === 'good' ? '▲' : move.tone === 'bad' ? '▼' : '•' }}</span>
              {{ move.title }}: <strong>{{ move.detail }}</strong>
            </li>
          }
        </ol>
      </section>
    }
  `,
  styles: `
    .recent {
      margin-top: 0.5rem;
    }
    h3 {
      margin: 0 0 0.25rem;
      font-size: 0.8rem;
      text-transform: uppercase;
      letter-spacing: 0.04em;
      color: var(--text-muted);
    }
    ol {
      display: grid;
      gap: 0.2rem;
      margin: 0;
      padding: 0;
      list-style: none;
      font-size: 0.875rem;
    }
    li:first-child {
      font-weight: 600;
    }
    .mark {
      display: inline-block;
      width: 1em;
    }
    .good .mark,
    .good strong {
      color: #1a7f37;
    }
    .bad .mark,
    .bad strong {
      color: #c4320a;
    }
  `,
})
export class RecentMoves {
  readonly moves = input.required<MoveNews[]>();
}
