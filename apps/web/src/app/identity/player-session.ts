import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/** Mirrors `CurrentPlayerResponse` from `GET /api/me` and `POST /api/guest-sessions`. */
export interface CurrentPlayer {
  playerId: string;
  displayName: string;
  kind: 'Guest';
}

export type PlayerSessionStatus = 'loading' | 'ready' | 'error';

/**
 * Establishes the browser's guest identity. The session lives in an HttpOnly cookie set by the
 * server, so this service never sees or stores a token or player id as proof of identity.
 */
@Injectable({ providedIn: 'root' })
export class PlayerSession {
  private readonly http = inject(HttpClient);

  readonly player = signal<CurrentPlayer | null>(null);
  readonly status = signal<PlayerSessionStatus>('loading');

  /** Reflects a display name change made through the profile, so the header stays in sync. */
  updateDisplayName(displayName: string): void {
    this.player.update((player) => (player ? { ...player, displayName } : player));
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
