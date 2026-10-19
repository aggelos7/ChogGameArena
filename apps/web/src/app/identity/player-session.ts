import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Avatar } from '../profile/profile-api';

/** Mirrors `CurrentPlayerResponse` from `GET /api/me`, `POST /api/guest-sessions` and `POST /api/wallet/verify`. */
export interface CurrentPlayer {
  playerId: string;
  displayName: string;
  kind: 'Guest' | 'Wallet';
  /** Set by the server only after a verified wallet sign-in (lowercase hex). */
  walletAddress: string | null;
}

export type PlayerSessionStatus = 'loading' | 'ready' | 'error';

/**
 * Establishes the browser's identity: a guest, or a wallet player after wallet sign-in. The session lives in an HttpOnly cookie set by the
 * server, so this service never sees or stores a token or player id as proof of identity.
 */
@Injectable({ providedIn: 'root' })
export class PlayerSession {
  private readonly http = inject(HttpClient);

  readonly player = signal<CurrentPlayer | null>(null);
  readonly status = signal<PlayerSessionStatus>('loading');
  /** The player's avatar for the header, from their profile; null until it has loaded. */
  readonly avatar = signal<Avatar | null>(null);

  /** Reflects a display name change made through the profile, so the header stays in sync. */
  updateDisplayName(displayName: string): void {
    this.player.update((player) => (player ? { ...player, displayName } : player));
  }

  /** Reflects an avatar or photo change made through the profile, or the profile once loaded. */
  updateAvatar(avatar: Avatar | null): void {
    this.avatar.set(avatar);
  }

  /** Adopts the identity the server returned after a session change (e.g. wallet sign-in). */
  setPlayer(player: CurrentPlayer): void {
    this.player.set(player);
    this.status.set('ready');
  }

  /** Ends the server session, then continues as a fresh guest. */
  async signOut(): Promise<void> {
    await firstValueFrom(this.http.post<void>('/api/wallet/sign-out', null));
    this.player.set(null);
    await this.ensureGuest();
  }

  /** Resumes the existing session, or creates a guest only when the server reports none (401). Never throws. */
  async ensureGuest(): Promise<void> {
    this.status.set('loading');
    try {
      this.player.set(await this.currentOrNewGuest());
      this.status.set('ready');
    } catch {
      this.player.set(null);
      this.status.set('error');
    }
  }

  private async currentOrNewGuest(): Promise<CurrentPlayer> {
    try {
      return await firstValueFrom(this.http.get<CurrentPlayer>('/api/me'));
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 401) {
        return firstValueFrom(this.http.post<CurrentPlayer>('/api/guest-sessions', {}));
      }
      throw error;
    }
  }
}
