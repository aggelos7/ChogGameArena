import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

/** Mirrors `AvatarResponse`. Placeholder avatars are drawn from their colour until artwork exists. */
export interface Avatar {
  id: string;
  name: string;
  color: string;
  /**
   * The player's uploaded photo, served by the API as a small square WebP (STORY-062, DEC-018). Shown instead of the
   * colour while there is one; null or absent otherwise.
   */
  imageUrl?: string | null;
}

/**
 * Mirrors `CollectorBadgeResponse` (STORY-060, DEC-065): a cosmetic collector tier for holding Chog NFTs, all that other
 * players see of someone's holdings. Purely visual.
 */
export interface CollectorBadge {
  id: string;
  name: string;
}

/** Mirrors `CollectorMilestoneResponse`: a collector tier and the Chogs it takes. */
export interface CollectorMilestone extends CollectorBadge {
  minChogs: number;
}

/** Mirrors `CollectorResponse`: the player's own Chogs held at the last gallery read, their tier and the next one. */
export interface CollectorProgress {
  chogsHeld: number;
  badge: CollectorBadge | null;
  /** The next milestone; null at the top tier. */
  next: CollectorMilestone | null;
  /** Every tier, from the lowest. */
  tiers: CollectorMilestone[];
}

/** Mirrors `ProfileResponse` from `GET/PATCH /api/profile`. Counters are maintained by the server only. */
export interface Profile {
  playerId: string;
  displayName: string;
  kind: 'Guest' | 'Wallet';
  avatar: Avatar;
  gamesPlayed: number;
  wins: number;
  /** All the XP earned from completed matches (STORY-059, DEC-064). */
  xp: number;
  collector: CollectorProgress;
  /** Whether the player accepted the current terms of use, which uploading a photo needs (STORY-069, DEC-070). */
  termsAccepted: boolean;
  /** Whether reports hid the player's photo from others until an operator reviews it. */
  avatarUnderReview: boolean;
  /** Whether the player reviews reported photos. */
  operator: boolean;
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

/** Photo types the server accepts; it checks the content itself, whatever the file says it is. */
export const PHOTO_TYPES = ['image/jpeg', 'image/png', 'image/webp'];
/** The server's default `AvatarImages:MaxUploadBytes`; it remains the authority. */
export const PHOTO_MAX_BYTES = 2 * 1024 * 1024;

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

  /** Accepts the terms of use in the version the player was shown; the server refuses an outdated one. */
  acceptTerms(version: string): Observable<Profile> {
    return this.http.post<Profile>('/api/profile/terms', { version });
  }

  /** Sends the file itself as the body; the server re-encodes it and replaces any earlier photo. */
  uploadPhoto(photo: Blob): Observable<Profile> {
    return this.http.put<Profile>('/api/profile/avatar-image', photo, { headers: { 'Content-Type': photo.type } });
  }

  /** Deletes the photo, so the built-in avatar shows again. */
  removePhoto(): Observable<Profile> {
    return this.http.delete<Profile>('/api/profile/avatar-image');
  }
}
