import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { Avatar, CollectorBadge } from '../profile/profile-api';

/** The board across every game; any other value is a catalog game id. */
export const ALL_GAMES = 'all';

export type LeaderboardPeriod = 'all-time' | 'season';

/** Mirrors `LeaderboardEntryResponse` (STORY-059, DEC-064): a player by name and avatar only, never an id. */
export interface LeaderboardEntry {
  place: number;
  displayName: string;
  avatar: Avatar;
  /** The player's cosmetic collector badge (DEC-065); null below the first tier. */
  collector: CollectorBadge | null;
  xp: number;
  wins: number;
  isYou: boolean;
}

/** Mirrors `LeaderboardResponse` from `GET /api/leaderboards`. */
export interface Leaderboard {
  game: string;
  period: LeaderboardPeriod;
  /** The UTC calendar month counted (ISO 8601, end exclusive); null for all-time. */
  season: { startsAt: string; endsAt: string } | null;
  /** The top places, in order. */
  entries: LeaderboardEntry[];
  /** The viewer's place, also below the top; null while they have no XP on this board. */
  you: LeaderboardEntry | null;
}

/** XP leaderboards, ranked by the server; the viewer comes from the session. */
@Injectable({ providedIn: 'root' })
export class LeaderboardApi {
  private readonly http = inject(HttpClient);

  get(game: string, period: LeaderboardPeriod): Observable<Leaderboard> {
    return this.http.get<Leaderboard>('/api/leaderboards', { params: { game, period } });
  }
}
