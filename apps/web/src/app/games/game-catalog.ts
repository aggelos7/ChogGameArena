import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

/** Mirrors `GameStatus` in ChogGames.Core. Only `Available` games can be launched. */
export type GameStatus = 'Available' | 'ComingSoon' | 'Maintenance';

/** Mirrors `GameCatalogItem` returned by `GET /api/games`. */
export interface GameSummary {
  id: string;
  name: string;
  description: string;
  status: GameStatus;
}

export const GAME_STATUS_LABELS: Record<GameStatus, string> = {
  Available: 'Available',
  ComingSoon: 'Coming soon',
  Maintenance: 'Under maintenance',
};

@Injectable({ providedIn: 'root' })
export class GameCatalog {
  private readonly http = inject(HttpClient);

  /** Always asks the server: availability can change at any time (e.g. maintenance). */
  getGames(): Observable<GameSummary[]> {
    return this.http.get<GameSummary[]>('/api/games');
  }
}
