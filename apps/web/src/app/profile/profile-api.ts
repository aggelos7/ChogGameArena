import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

/** Mirrors `AvatarResponse`. Placeholder avatars are drawn from their colour until artwork exists. */
export interface Avatar {
  id: string;
  name: string;
  color: string;
}

/** Mirrors `ProfileResponse` from `GET/PATCH /api/profile`. Counters are maintained by the server only. */
export interface Profile {
  playerId: string;
  displayName: string;
  kind: 'Guest' | 'Wallet';
  avatar: Avatar;
  gamesPlayed: number;
  wins: number;
}

/**
 * Mirrors `AchievementResponse` from `GET /api/profile/achievements` (STORY-058, DEC-063): one achievement of the
 * catalog, unlocked by the server from completed matches only. An unlocked one shows its target as its progress.
 */
export interface Achievement {
  id: string;
  name: string;
  description: string;
  progress: number;
  target: number;
  /** When it was unlocked (ISO 8601); null while locked. */
  unlockedAt: string | null;
}

export interface ProfileUpdate {
  displayName?: string;
  avatarId?: string;
}

/** Mirrors `PlayerDisplayName` rules on the server (which remains the authority). */
export const DISPLAY_NAME_PATTERN = /^[\p{L}\p{Nd}_-]+( [\p{L}\p{Nd}_-]+)*$/u;
export const DISPLAY_NAME_MIN = 3;
export const DISPLAY_NAME_MAX = 20;
export const DISPLAY_NAME_RULE =
  "3–20 characters: letters, digits, single spaces, '_' or '-'.";

/** Always the signed-in player's own profile: no player id is ever sent. */
@Injectable({ providedIn: 'root' })
export class ProfileApi {
  private readonly http = inject(HttpClient);

  get(): Observable<Profile> {
    return this.http.get<Profile>('/api/profile');
  }

  achievements(): Observable<Achievement[]> {
    return this.http.get<Achievement[]>('/api/profile/achievements');
  }

  avatars(): Observable<Avatar[]> {
    return this.http.get<Avatar[]>('/api/profile/avatars');
  }

  update(changes: ProfileUpdate): Observable<Profile> {
    return this.http.patch<Profile>('/api/profile', changes);
  }
}
