import { Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { GAME_STATUS_LABELS, GameCatalog, GameSummary } from '../../games/game-catalog';
import { EmptyState, ErrorState, LoadingState } from '../../shared/ui/state-views';

/** Each game's picture, the one its match is played over, shown behind its card. */
const GAME_PICTURES: Record<string, string> = {
  'chog-life': '/chog-life/background.webp',
  'chog-race': '/chog-race/background.webp',
};

type CatalogueState =
  | { kind: 'loading' }
  | { kind: 'error' }
  | { kind: 'loaded'; games: GameSummary[] };

@Component({
  selector: 'app-home',
  imports: [RouterLink, LoadingState, ErrorState, EmptyState],
  templateUrl: './home.html',
  styleUrl: './home.scss',
})
export class Home implements OnInit {
  private readonly catalog = inject(GameCatalog);

  protected readonly state = signal<CatalogueState>({ kind: 'loading' });
  protected readonly statusLabels = GAME_STATUS_LABELS;

  protected picture(game: GameSummary): string | null {
    const url = GAME_PICTURES[game.id];
    return url ? `url('${url}')` : null;
  }

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.state.set({ kind: 'loading' });
    this.catalog.getGames().subscribe({
      next: (games) => this.state.set({ kind: 'loaded', games }),
      error: () => this.state.set({ kind: 'error' }),
    });
  }
}
