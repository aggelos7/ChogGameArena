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
  kind: 'Guest';
  avatar: Avatar;
  gamesPlayed: number;
  wins: number;
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

  avatars(): Observable<Avatar[]> {
    return this.http.get<Avatar[]>('/api/profile/avatars');
  }

  update(changes: ProfileUpdate): Observable<Profile> {
    return this.http.patch<Profile>('/api/profile', changes);
  }
}
