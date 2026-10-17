import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Subscription } from 'rxjs';
import { GameCatalog } from '../../games/game-catalog';
import { ALL_GAMES, Leaderboard, LeaderboardApi, LeaderboardPeriod } from '../../leaderboards/leaderboard-api';
import { AvatarBadge } from '../../profile/avatar-badge';
import { CollectorChip } from '../../profile/collector-chip';
import { ErrorState, LoadingState } from '../../shared/ui/state-views';

interface BoardChoice<T> {
  value: T;
  label: string;
}

/**
 * XP leaderboards (STORY-059, DEC-064): overall or one game, all-time or this season (a UTC calendar month). Shows the
 * top places and the viewer's own place when it's below them.
 */
@Component({
  selector: 'app-leaderboard',
  imports: [DatePipe, NgTemplateOutlet, AvatarBadge, CollectorChip, LoadingState, ErrorState],
  templateUrl: './leaderboard.html',
  styleUrl: './leaderboard.scss',
})
export class LeaderboardPage implements OnInit {
  private readonly api = inject(LeaderboardApi);
  private readonly catalog = inject(GameCatalog);
  private request?: Subscription;

  protected readonly periods: BoardChoice<LeaderboardPeriod>[] = [
    { value: 'all-time', label: 'All-time' },
    { value: 'season', label: 'This season' },
  ];
  protected readonly games = signal<BoardChoice<string>[]>([{ value: ALL_GAMES, label: 'All games' }]);
  protected readonly game = signal(ALL_GAMES);
  protected readonly period = signal<LeaderboardPeriod>('all-time');
  protected readonly board = signal<Leaderboard | null>(null);
  protected readonly loadState = signal<'loading' | 'ready' | 'error'>('loading');

  /** The viewer's place when the top places don't include it. */
  protected readonly ownPlaceBelow = computed(() => {
    const board = this.board();
    return board?.you && !board.entries.some((entry) => entry.isYou) ? board.you : null;
  });

  ngOnInit(): void {
    // The boards work without the catalog; it only adds a board per game.
    this.catalog.getGames().subscribe({
      next: (games) => this.games.set([this.games()[0], ...games.map((g) => ({ value: g.id, label: g.name }))]),
      error: () => undefined,
    });
    this.load();
  }

  protected choose(game: string, period: LeaderboardPeriod): void {
    if (game !== this.game() || period !== this.period()) {
      this.game.set(game);
      this.period.set(period);
      this.load();
    }
  }

  protected load(): void {
    this.request?.unsubscribe();
    this.loadState.set('loading');
    this.request = this.api.get(this.game(), this.period()).subscribe({
      next: (board) => {
        this.board.set(board);
        this.loadState.set('ready');
      },
      error: () => this.loadState.set('error'),
    });
  }
}
