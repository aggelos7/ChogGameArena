import { Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { GAME_STATUS_LABELS, GameCatalog, GameSummary } from '../../games/game-catalog';
import { EmptyState, ErrorState, LoadingState } from '../../shared/ui/state-views';

type CatalogueState =
  | { kind: 'loading' }
  | { kind: 'error' }
  | { kind: 'loaded'; games: GameSummary[] };

@Component({
  selector: 'app-games',
  imports: [RouterLink, LoadingState, ErrorState, EmptyState],
  templateUrl: './games.html',
  styleUrl: './games.scss',
})
export class Games implements OnInit {
  private readonly catalog = inject(GameCatalog);

  protected readonly state = signal<CatalogueState>({ kind: 'loading' });
  protected readonly statusLabels = GAME_STATUS_LABELS;

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
